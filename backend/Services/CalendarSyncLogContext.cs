using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

// Display snapshots and optional owned-entry references for one synchronization page.
// Ownership only spans this save/detach batch; never cache context across connections.
internal sealed class CalendarSyncLogContext(int connectionId, User? user, IEnumerable<TaskJournalItem> tasks,
    ICollection<object>? ownedEntities = null)
{
    public int ConnectionId { get; } = connectionId;
    public string? UserName { get; } = user?.FullName;
    public string? UserEmail { get; } = user?.Email;
    public IReadOnlyDictionary<int, TaskFields> Tasks { get; } = tasks.ToDictionary(
        task => task.Id, task => new TaskFields(task.Title, task.ObjectName, task.AssigneeName));
    public Dictionary<int, CalendarFields?> Calendars { get; } = [];

    public void RememberOwnedEntity(object entity) => ownedEntities?.Add(entity);

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
