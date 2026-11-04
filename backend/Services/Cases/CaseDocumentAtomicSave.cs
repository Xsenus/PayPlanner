using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services;

// The persistence owner signals before attempting COMMIT, including a lost acknowledgement.
public delegate Task<T> CaseDocumentAtomicPersistence<T>(Func<Task<T>> saveRecords, Action commitStarting, CancellationToken ct);

/// <summary>
/// Saves a case document graph and restores only this operation's tracking after a confirmed rollback.
/// Shared by HTTP writers and durable publication consumers.
/// </summary>
public static class CaseDocumentAtomicSave
{
    public static async Task<T> SaveAsync<T>(
        PaymentContext db, ICaseDocumentStorage storage, string relativePath, Func<Task> saveFile,
        Func<Task<T>> saveRecords, CancellationToken ct, bool preserveFileOnFailure = false,
        CaseDocumentAtomicPersistence<T>? persistence = null)
    {
        db.ChangeTracker.DetectChanges();
        var trackedBefore = db.ChangeTracker.Entries().Select(entry => new CaseDocumentTrackingSnapshot(
            entry.Entity, entry.State, entry.CurrentValues.Clone(), entry.OriginalValues.Clone(),
            entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name).ToArray(),
            entry.Properties.Where(property => property.IsTemporary).Select(property => property.Metadata.Name).ToArray())).ToArray();
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        var fileSaved = false;
        var commitStarted = false;
        try
        {
            await saveFile();
            fileSaved = true;
            if (persistence is not null)
                return await persistence(saveRecords, () => commitStarted = true, ct);
            transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await saveRecords();
            commitStarted = true;
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception exception)
        {
            // Never remove bytes after commit was attempted: a lost acknowledgement can
            // leave a committed reference. Retaining an orphan is safer than losing a document.
            var rollbackConfirmed = transaction is null && !commitStarted;
            if (transaction is not null)
            {
                try
                {
                    using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await transaction.RollbackAsync(rollbackTimeout.Token);
                    rollbackConfirmed = true;
                }
                catch (Exception rollbackException)
                {
                    // Keep the original failure and the uncertain rollback evidence.
                    exception.Data["CaseDocumentRollbackFailure"] = rollbackException;
                }
            }
            // Before any commit attempt this operation cannot have committed its graph.
            // With lost commit ACK, leave the accepted graph intact unless rollback is confirmed.
            if (rollbackConfirmed || !commitStarted) RestoreCaseDocumentTracking(db, trackedBefore);
            if (!preserveFileOnFailure && !commitStarted && rollbackConfirmed && fileSaved) storage.DeleteIfExists(relativePath);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private sealed record CaseDocumentTrackingSnapshot(object Entity, EntityState State,
        PropertyValues CurrentValues, PropertyValues OriginalValues, string[] ModifiedProperties, string[] TemporaryProperties);

    private static void RestoreCaseDocumentTracking(PaymentContext db, IReadOnlyList<CaseDocumentTrackingSnapshot> snapshots)
    {
        var originalEntities = snapshots.Select(snapshot => snapshot.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var entry in db.ChangeTracker.Entries().ToArray())
        {
            if (!originalEntities.Contains(entry.Entity)) entry.State = EntityState.Detached;
        }
        foreach (var snapshot in snapshots)
        {
            var entry = db.Entry(snapshot.Entity);
            // Added entries may have acquired permanent keys before the transaction rolled back.
            // Detach before restoring their keys, then let EF track the original pending insert.
            if (snapshot.State == EntityState.Added) entry.State = EntityState.Detached;
            entry.CurrentValues.SetValues(snapshot.CurrentValues);
            entry.OriginalValues.SetValues(snapshot.OriginalValues);
            entry.State = snapshot.State;
            foreach (var property in entry.Properties)
            {
                property.IsModified = snapshot.ModifiedProperties.Contains(property.Metadata.Name, StringComparer.Ordinal);
                property.IsTemporary = snapshot.TemporaryProperties.Contains(property.Metadata.Name, StringComparer.Ordinal);
            }
        }
    }
}
