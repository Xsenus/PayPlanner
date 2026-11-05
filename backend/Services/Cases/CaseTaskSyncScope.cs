using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Cases;

internal static class CaseTaskSyncScope
{
    private static readonly long PostgreSqlEpochTicks = new DateTime(2000, 1, 1).Ticks;

    private sealed record TrackingSnapshot(object Entity, EntityState State, PropertyValues Current,
        PropertyValues Original, string[] Modified, string[] Temporary);

    public static async Task<bool> RunAsync(PaymentContext db, int caseId, Func<Task<bool>> synchronize, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        var before = db.ChangeTracker.Entries().Select(entry => new TrackingSnapshot(entry.Entity, entry.State,
            entry.CurrentValues.Clone(), entry.OriginalValues.Clone(),
            entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name).ToArray(),
            entry.Properties.Where(property => property.IsTemporary).Select(property => property.Metadata.Name).ToArray())).ToArray();
        var caller = db.Database.CurrentTransaction;
        IDbContextTransaction? owned = null;
        string? savepoint = null;
        var commitStarted = false;
        try
        {
            if (caller is null) owned = await db.Database.BeginTransactionAsync(ct);
            else
            {
                if (!caller.SupportsSavepoints) throw new InvalidOperationException("Case task synchronization requires savepoint support.");
                savepoint = "case_task_sync_" + Guid.NewGuid().ToString("N");
                await caller.CreateSavepointAsync(savepoint, ct);
            }
            await PinInputsAsync(db, caseId, ct);
            await ValidateTrackedInputsAsync(db, caseId, before, ct);
            var changed = await synchronize();
            if (owned is not null)
            {
                commitStarted = true;
                await owned.CommitAsync(ct);
            }
            else await caller!.ReleaseSavepointAsync(savepoint!, ct);
            return changed;
        }
        catch (Exception error)
        {
            var restored = false;
            if (!commitStarted || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)
                || error is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    if (owned is not null) await owned.RollbackAsync(timeout.Token);
                    else if (savepoint is not null)
                    {
                        await caller!.RollbackToSavepointAsync(savepoint, timeout.Token);
                        await caller.ReleaseSavepointAsync(savepoint, timeout.Token);
                    }
                    restored = true;
                }
                catch (Exception rollbackError) { error.Data["CaseTaskSyncRollbackFailure"] = rollbackError; }
            }
            if (restored) RestoreTracking(db, before);
            throw;
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    private static Task PinInputsAsync(PaymentContext db, int caseId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return Task.CompletedTask;
        // The case row excludes new dependents. Existing rows stay pinned for
        // the owning transaction, including a caller-owned transaction.
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT "Id" FROM "ClientCases" WHERE "Id" = {caseId} FOR UPDATE NOWAIT;
            SELECT "Id" FROM "TaskJournalItems" WHERE "ClientCaseId" = {caseId} ORDER BY "Id" FOR UPDATE NOWAIT;
            SELECT "Id" FROM "TaskJournalParticipants" WHERE "TaskJournalItemId" IN
                (SELECT "Id" FROM "TaskJournalItems" WHERE "ClientCaseId" = {caseId}) ORDER BY "Id" FOR UPDATE NOWAIT;
            SELECT "Id" FROM "ClientCaseProceduralDeadlines" WHERE "ClientCaseId" = {caseId} ORDER BY "Id" FOR SHARE NOWAIT;
            SELECT "Id" FROM "ClientCaseCourtEvents" WHERE "ClientCaseId" = {caseId} ORDER BY "Id" FOR SHARE NOWAIT;
            SELECT "Id" FROM "ClientCaseCourtInstances" WHERE "ClientCaseId" = {caseId} ORDER BY "Id" FOR SHARE NOWAIT;
            SELECT "Id" FROM "ClientCaseCourtComplaints" WHERE "ClientCaseId" = {caseId} ORDER BY "Id" FOR SHARE NOWAIT;
            """, ct);
    }

    private static async Task ValidateTrackedInputsAsync(PaymentContext db, int caseId, TrackingSnapshot[] before, CancellationToken ct)
    {
        bool BelongsToCase(TrackingSnapshot snapshot) => Equals(snapshot.Original["ClientCaseId"], caseId)
            || Equals(snapshot.Current["ClientCaseId"], caseId);
        var participants = before.Where(item => item.Entity is TaskJournalParticipant).ToArray();
        var taskIds = before.Where(item => item.Entity is TaskJournalItem && BelongsToCase(item))
            .Select(item => (int)item.Original["Id"]!).ToHashSet();
        if (participants.Length > 0)
            taskIds.UnionWith(await db.TaskJournalItems.AsNoTracking().Where(row => row.ClientCaseId == caseId).Select(row => row.Id).ToArrayAsync(ct));
        await ValidateAsync(db, db.ClientCases.Where(row => row.Id == caseId), before.Where(item => item.Entity is ClientCase && Equals(item.Original["Id"], caseId)), ct);
        await ValidateAsync(db, db.TaskJournalItems.Where(row => row.ClientCaseId == caseId), before.Where(item => item.Entity is TaskJournalItem && BelongsToCase(item)), ct);
        await ValidateAsync(db, db.TaskJournalParticipants.Where(row => row.TaskJournalItem.ClientCaseId == caseId),
            participants.Where(item => taskIds.Contains((int)item.Original["TaskJournalItemId"]!) || taskIds.Contains((int)item.Current["TaskJournalItemId"]!)), ct);
        await ValidateAsync(db, db.ClientCaseProceduralDeadlines.Where(row => row.ClientCaseId == caseId), before.Where(item => item.Entity is ClientCaseProceduralDeadline && BelongsToCase(item)), ct);
        await ValidateAsync(db, db.ClientCaseCourtEvents.Where(row => row.ClientCaseId == caseId), before.Where(item => item.Entity is ClientCaseCourtEvent && BelongsToCase(item)), ct);
        await ValidateAsync(db, db.ClientCaseCourtComplaints.Where(row => row.ClientCaseId == caseId && row.CourtInstance.ClientCaseId == caseId),
            before.Where(item => item.Entity is ClientCaseCourtComplaint && BelongsToCase(item)), ct, allowMissingUnchanged: true);
    }

    private static async Task ValidateAsync<TEntity>(PaymentContext db, IQueryable<TEntity> query, IEnumerable<TrackingSnapshot> snapshots, CancellationToken ct,
        bool allowMissingUnchanged = false)
        where TEntity : class
    {
        var tracked = snapshots.Where(item => item.State != EntityState.Added).ToDictionary(item => (int)item.Original["Id"]!);
        if (tracked.Count == 0) return;
        var ids = tracked.Keys.ToArray();
        var properties = db.Model.FindEntityType(typeof(TEntity))!.GetProperties().ToArray();
        var parameter = Expression.Parameter(typeof(TEntity), "row");
        var propertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
        var selector = Expression.Lambda<Func<TEntity, object?[]>>(Expression.NewArrayInit(typeof(object), properties.Select(property =>
            Expression.Convert(Expression.Call(propertyMethod.MakeGenericMethod(property.ClrType), parameter, Expression.Constant(property.Name)), typeof(object)))), parameter);
        var rows = await query.AsNoTracking().Where(row => ids.Contains(EF.Property<int>(row, "Id"))).Select(selector).ToArrayAsync(ct);
        var idIndex = Array.FindIndex(properties, property => property.Name == "Id");
        if (rows.Length != tracked.Count)
        {
            var found = rows.Select(row => (int)row[idIndex]!).ToHashSet();
            if (!allowMissingUnchanged || tracked.Any(item => !found.Contains(item.Key) && item.Value.State != EntityState.Unchanged)) throw Changed();
        }
        foreach (var row in rows)
        {
            var original = tracked[(int)row[idIndex]!].Original;
            for (var index = 0; index < properties.Length; index++)
                if (!StoredEquals(db, properties[index], row[index], original[properties[index].Name]))
                {
                    var error = Changed();
                    error.Data["CaseTaskSyncField"] = typeof(TEntity).Name + "." + properties[index].Name;
                    throw error;
                }
        }
    }

    private static bool StoredEquals(PaymentContext db, IProperty property, object? actual, object? expected)
    {
        if (db.Database.IsNpgsql())
        {
            // Npgsql truncates microseconds relative to 2000, including dates before that epoch.
            if (actual is DateTime date && expected is DateTime oldDate)
                return (date.Ticks - PostgreSqlEpochTicks) / 10 == (oldDate.Ticks - PostgreSqlEpochTicks) / 10;
            if (actual is DateTimeOffset offset && expected is DateTimeOffset oldOffset)
                return (offset.UtcTicks - PostgreSqlEpochTicks) / 10 == (oldOffset.UtcTicks - PostgreSqlEpochTicks) / 10;
            if (actual is TimeSpan span && expected is TimeSpan oldSpan) return span.Ticks / 10 == oldSpan.Ticks / 10;
            var scale = property.GetScale() ?? property.GetRelationalTypeMapping().Scale;
            if (actual is decimal amount && expected is decimal oldAmount && scale is >= 0 and <= 28)
                return amount == Math.Round(oldAmount, scale.Value, MidpointRounding.AwayFromZero);
        }
        return property.GetValueComparer()?.Equals(actual, expected) ?? Equals(actual, expected);
    }

    private static DbUpdateConcurrencyException Changed() => new("Данные дела или задач изменились. Перечитайте записи перед повторной синхронизацией.");

    private static void RestoreTracking(PaymentContext db, TrackingSnapshot[] before)
    {
        var originals = before.Select(item => item.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var entry in db.ChangeTracker.Entries().ToArray())
            if (!originals.Contains(entry.Entity)) entry.State = EntityState.Detached;
        foreach (var item in before)
        {
            var entry = db.Entry(item.Entity);
            if (item.State == EntityState.Added) entry.State = EntityState.Detached;
            entry.CurrentValues.SetValues(item.Current);
            entry.OriginalValues.SetValues(item.Original);
            entry.State = item.State;
            foreach (var property in entry.Properties)
            {
                property.IsModified = item.Modified.Contains(property.Metadata.Name, StringComparer.Ordinal);
                property.IsTemporary = item.Temporary.Contains(property.Metadata.Name, StringComparer.Ordinal);
            }
        }
    }
}
