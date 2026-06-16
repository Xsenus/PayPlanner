using System.Security.Claims;
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
        if (!int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId <= 0)
        {
            Context.Abort();
            return;
        }

        var canViewNotes = await _db.HasUserSectionPermissionAsync(
            Context.User,
            RolePermissionCatalog.Notes,
            static permission => permission.CanView,
            Context.ConnectionAborted);
        if (!canViewNotes)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public async Task JoinBoard(int boardId)
    {
        if (boardId <= 0)
        {
            return;
        }

        var userId = Context.User?.GetUserId();
        if (!userId.HasValue || !await _db.HasUserSectionPermissionAsync(Context.User!, RolePermissionCatalog.Notes, static permission => permission.CanView, Context.ConnectionAborted))
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
}
