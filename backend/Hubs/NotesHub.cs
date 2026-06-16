using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PayPlanner.Api.Hubs;

[Authorize]
public sealed class NotesHub : Hub
{
    public const string Path = "/hubs/notes";
    public const string BoardChangedEvent = "stickyNoteBoardChanged";

    public static string BoardGroup(int boardId) => $"sticky-notes:board:{boardId}";

    public override async Task OnConnectedAsync()
    {
        if (!int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId <= 0)
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public Task JoinBoard(int boardId)
    {
        if (boardId <= 0)
        {
            return Task.CompletedTask;
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, BoardGroup(boardId));
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
