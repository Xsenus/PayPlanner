using System.Security.Claims;
using System.Text.Json;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class CalendarExportAudit
{
    public const string ObjectType = "calendar-sync";
    public const string ExportedAction = "Exported";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ObjectAuditEvent CreateExportEvent(
        ClaimsPrincipal user,
        string calendarKind,
        string description,
        int exportedEventCount,
        IReadOnlyList<int> reminderMinutes,
        object filters)
        => new()
        {
            ObjectType = ObjectType,
            ObjectId = 0,
            Action = ExportedAction,
            Description = description,
            ActorUserId = user.GetUserId(),
            ActorUserName = user.GetDisplayName(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                calendarKind,
                exportedEventCount,
                reminderMinutes,
                filters,
            }, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        };
}
