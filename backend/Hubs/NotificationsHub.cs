using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PayPlanner.Api.Hubs;

[Authorize]
public sealed class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";
    public const string UserNotificationCreatedEvent = "userNotificationCreated";
    public const string TaskJournalChangedEvent = "taskJournalChanged";
    public const string RolePermissionsChangedEvent = "rolePermissionsChanged";
    public const string UserRoleChangedEvent = "userRoleChanged";

    public static string UserGroup(int userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        if (!int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId <= 0)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        await base.OnConnectedAsync();
    }
}
