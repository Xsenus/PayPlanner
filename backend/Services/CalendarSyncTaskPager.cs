using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

internal static class CalendarSyncTaskPager
{
    internal const int PageSize = 500;

    public static HashSet<object> CaptureTrackedEntities(DbContext db) =>
        db.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);

    public static IReadOnlyList<TLink> DetachSavedBatch<TLink, TLog>(DbContext db, HashSet<object> preserved,
        IEnumerable<object> ownedEntities)
        where TLink : class
        where TLog : class
    {
        var detachedLinks = new List<TLink>();
        var entries = ownedEntities.Distinct(ReferenceEqualityComparer.Instance)
            .Select(entity => db.Entry(entity))
            .Where(entry => (entry.Entity is TLink or TLog)
                && entry.State == EntityState.Unchanged && !preserved.Contains(entry.Entity))
            .ToArray();
        foreach (var entry in entries)
        {
            if (entry.Entity is TLink link)
            {
                detachedLinks.Add(link);
            }
            entry.State = EntityState.Detached;
        }
        return detachedLinks;
    }

    public static async IAsyncEnumerable<IReadOnlyList<TaskJournalItem>> ReadPagesAsync(
        IQueryable<TaskJournalItem> query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Freeze the upper ID: tasks created during a long sync belong to the next run.
        var upperId = await query.MaxAsync(item => (int?)item.Id, cancellationToken) ?? 0;
        var lastId = 0;
        while (lastId < upperId)
        {
            var page = await query.AsNoTracking()
                .Where(item => item.Id > lastId && item.Id <= upperId)
                .OrderBy(item => item.Id)
                .Take(PageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                yield break;
            }

            lastId = page[^1].Id;
            yield return page;
        }
    }
}
