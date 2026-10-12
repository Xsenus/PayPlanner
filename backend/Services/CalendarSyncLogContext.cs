using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

// One export page (or one addressed task), passed explicitly and released after the operation.
// Keep display snapshots only; do not retain entity graphs or cache across connections.
internal sealed class CalendarSyncLogContext(int connectionId, User? user, IEnumerable<TaskJournalItem> tasks)
{
    public int ConnectionId { get; } = connectionId;
    public string? UserName { get; } = user?.FullName;
    public string? UserEmail { get; } = user?.Email;
    public IReadOnlyDictionary<int, TaskFields> Tasks { get; } = tasks.ToDictionary(
        task => task.Id, task => new TaskFields(task.Title, task.ObjectName, task.AssigneeName));
    public Dictionary<int, CalendarFields?> Calendars { get; } = [];

    public void RememberCalendar(int calendarId, string? name, int? ownerUserId, User? owner)
    {
        if (Calendars.ContainsKey(calendarId) || (ownerUserId.HasValue && owner?.Id != ownerUserId.Value))
        {
            return;
        }
        Calendars.Add(calendarId, new CalendarFields(name,
            ownerUserId.HasValue ? owner?.FullName : null, ownerUserId.HasValue ? owner?.Email : null));
    }

    internal sealed record TaskFields(string? Title, string? ObjectName, string? AssigneeName);
    internal sealed record CalendarFields(string? Name, string? OwnerName, string? OwnerEmail);
}
