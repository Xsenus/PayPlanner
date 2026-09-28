using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupCatalogPage(IReadOnlyList<DatabaseBackupSet> Items, string? NextCursor);

public sealed class DatabaseBackupCatalogReader(PaymentContext context)
{
    public async Task<DatabaseBackupCatalogPage> ReadAsync(
        string? after = null, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Размер страницы должен быть от 1 до 100.");
        if (after is not null && (string.IsNullOrWhiteSpace(after) || after.Length > 64))
            throw new ArgumentException("Некорректная позиция в журнале копий.", nameof(after));

        var query = context.DatabaseBackupSets.AsNoTracking();
        if (after is not null)
        {
            var pivot = await query.Where(item => item.Id == after)
                .Select(item => new { item.Id, item.CreatedAt }).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException("Позиция в журнале копий больше не найдена. Обновите журнал.", nameof(after));
            query = query.Where(item => item.CreatedAt < pivot.CreatedAt
                || (item.CreatedAt == pivot.CreatedAt && string.Compare(item.Id, pivot.Id) < 0));
        }

        var items = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(pageSize + 1).Include(item => item.Copies).AsSplitQuery().ToListAsync(cancellationToken);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new DatabaseBackupCatalogPage(items, hasMore ? items[^1].Id : null);
    }
}
