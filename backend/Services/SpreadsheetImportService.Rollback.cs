using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private async Task<bool> RollbackCounterpartyAsync(PaymentContext db, SpreadsheetImportBatchOperation operation, CancellationToken cancellationToken)
    {
        if (!operation.EntityId.HasValue)
        {
            operation.RollbackAction = SpreadsheetImportRollbackAction.Error;
            operation.RollbackMessage = "Для операции не сохранен идентификатор контрагента.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var metadata = DeserializeSnapshot<CounterpartyOperationMetadata>(operation.MetadataJson) ?? new CounterpartyOperationMetadata();
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Include(item => item.LegalEntity)
            .FirstOrDefaultAsync(item => item.Id == operation.EntityId.Value, cancellationToken);

        if (client is null)
        {
            operation.RollbackAction = SpreadsheetImportRollbackAction.Skipped;
            operation.RollbackMessage = "Контрагент уже отсутствует в базе.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var legalEntity = client.LegalEntity;
        if (legalEntity is null && metadata.LegalEntityId.HasValue)
        {
            legalEntity = await db.LegalEntities
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == metadata.LegalEntityId.Value, cancellationToken);
        }

        var currentJson = SerializeSnapshot(CaptureCounterpartySnapshot(client, legalEntity));
        if (!string.Equals(currentJson, operation.AfterJson, StringComparison.Ordinal))
        {
            operation.RollbackAction = SpreadsheetImportRollbackAction.Skipped;
            operation.RollbackMessage = "Откат пропущен: контрагент или связанное юридическое лицо были изменены после импорта.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        try
        {
            if (operation.Action == SpreadsheetImportOperationAction.Created)
            {
                if (await IsClientStillReferencedAsync(db, client.Id, cancellationToken))
                {
                    operation.RollbackAction = SpreadsheetImportRollbackAction.Skipped;
                    operation.RollbackMessage = "Откат контрагента пропущен: запись используется связанными документами.";
                    operation.RolledBackAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    return false;
                }

                if (metadata.LegalEntityCreated
                    && legalEntity is not null
                    && await IsLegalEntityStillReferencedAsync(db, legalEntity.Id, client.Id, cancellationToken))
                {
                    operation.RollbackAction = SpreadsheetImportRollbackAction.Skipped;
                    operation.RollbackMessage = "Откат контрагента пропущен: созданное импортом юридическое лицо уже используется другими данными.";
                    operation.RolledBackAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    return false;
                }

                db.Remove(client);
                await db.SaveChangesAsync(cancellationToken);

                if (metadata.LegalEntityCreated && legalEntity is not null)
                {
                    var storedLegalEntity = await db.LegalEntities
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(item => item.Id == legalEntity.Id, cancellationToken);
                    if (storedLegalEntity is not null && !await IsLegalEntityStillReferencedAsync(db, storedLegalEntity.Id, null, cancellationToken))
                    {
                        db.Remove(storedLegalEntity);
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }

                operation.RollbackAction = SpreadsheetImportRollbackAction.Deleted;
                operation.RollbackMessage = metadata.LegalEntityCreated
                    ? "Созданные импортом клиент и юридическое лицо удалены."
                    : "Созданный импортом контрагент удален.";
                operation.RolledBackAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var snapshot = DeserializeSnapshot<CounterpartySnapshot>(operation.BeforeJson);
            if (snapshot?.Client is null)
            {
                operation.RollbackAction = SpreadsheetImportRollbackAction.Error;
                operation.RollbackMessage = "Не удалось восстановить исходный снимок контрагента.";
                operation.RolledBackAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            if (metadata.LegalEntityCreated
                && snapshot.LegalEntity is null
                && legalEntity is not null
                && await IsLegalEntityStillReferencedAsync(db, legalEntity.Id, client.Id, cancellationToken))
            {
                operation.RollbackAction = SpreadsheetImportRollbackAction.Skipped;
                operation.RollbackMessage = "Откат пропущен: созданное импортом юридическое лицо уже используется другими данными.";
                operation.RolledBackAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            RestoreClientSnapshot(client, snapshot.Client);

            if (snapshot.LegalEntity is not null)
            {
                var targetLegalEntity = legalEntity;
                if (targetLegalEntity is null && snapshot.LegalEntity.Id > 0)
                {
                    targetLegalEntity = await db.LegalEntities
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(item => item.Id == snapshot.LegalEntity.Id, cancellationToken);
                }

                targetLegalEntity ??= new LegalEntity();
                RestoreLegalEntitySnapshot(targetLegalEntity, snapshot.LegalEntity);
                if (targetLegalEntity.Id == 0)
                {
                    db.LegalEntities.Add(targetLegalEntity);
                }

                client.LegalEntity = targetLegalEntity;
                client.LegalEntityId = targetLegalEntity.Id > 0 ? targetLegalEntity.Id : null;
                await db.SaveChangesAsync(cancellationToken);

                if (!client.LegalEntityId.HasValue && targetLegalEntity.Id > 0)
                {
                    client.LegalEntityId = targetLegalEntity.Id;
                }
            }
            else
            {
                client.LegalEntity = null;
                client.LegalEntityId = null;
                await db.SaveChangesAsync(cancellationToken);

                if (metadata.LegalEntityCreated && legalEntity is not null)
                {
                    var storedLegalEntity = await db.LegalEntities
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(item => item.Id == legalEntity.Id, cancellationToken);
                    if (storedLegalEntity is not null && !await IsLegalEntityStillReferencedAsync(db, storedLegalEntity.Id, null, cancellationToken))
                    {
                        db.Remove(storedLegalEntity);
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            operation.RollbackAction = SpreadsheetImportRollbackAction.Restored;
            operation.RollbackMessage = metadata.LegalEntityCreated && snapshot.LegalEntity is null
                ? "Контрагент восстановлен, созданное импортом юридическое лицо удалено."
                : "Контрагент восстановлен к состоянию до импорта.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spreadsheet rollback failed for counterparty operation {OperationId}", operation.Id);
            db.ChangeTracker.Clear();

            var storedOperation = await db.SpreadsheetImportBatchOperations.FirstAsync(item => item.Id == operation.Id, cancellationToken);
            storedOperation.RollbackAction = SpreadsheetImportRollbackAction.Error;
            storedOperation.RollbackMessage = $"Не удалось выполнить откат: {ex.Message}";
            storedOperation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
    }

    private static async Task<bool> IsClientStillReferencedAsync(PaymentContext db, int clientId, CancellationToken cancellationToken)
    {
        if (await db.Payments.AnyAsync(item => item.ClientId == clientId || item.ExpenseClientId == clientId, cancellationToken))
        {
            return true;
        }

        if (await db.Acts.AnyAsync(item => item.ClientId == clientId, cancellationToken))
        {
            return true;
        }

        if (await db.ClientCases.AnyAsync(item => item.ClientId == clientId, cancellationToken))
        {
            return true;
        }

        return false;
    }

    private static async Task<bool> IsLegalEntityStillReferencedAsync(
        PaymentContext db,
        int legalEntityId,
        int? excludeClientId,
        CancellationToken cancellationToken)
    {
        if (await db.Clients.IgnoreQueryFilters().AnyAsync(
                item => item.LegalEntityId == legalEntityId && (!excludeClientId.HasValue || item.Id != excludeClientId.Value),
                cancellationToken))
        {
            return true;
        }

        if (await db.ExternalContacts.IgnoreQueryFilters().AnyAsync(item => item.LegalEntityId == legalEntityId, cancellationToken))
        {
            return true;
        }

        if (await db.AddressRecords.IgnoreQueryFilters().AnyAsync(item => item.LegalEntityId == legalEntityId, cancellationToken))
        {
            return true;
        }

        if (await db.UserCompanyAccesses.IgnoreQueryFilters().AnyAsync(item => item.LegalEntityId == legalEntityId, cancellationToken))
        {
            return true;
        }

        return false;
    }

    private async Task<bool> RollbackInvoiceAsync(PaymentContext db, SpreadsheetImportBatchOperation operation, CancellationToken cancellationToken)
    {
        return await RollbackEntityAsync<Payment, InvoiceSnapshot>(
            db,
            operation,
            cancellationToken,
            async id => await db.Payments
                .IgnoreQueryFilters()
                .Include(item => item.Invoice)
                .Include(item => item.InvoiceLineItems)
                .Include(item => item.Responsibles)
                .FirstOrDefaultAsync(item => item.Id == id, cancellationToken),
            entity => SerializeSnapshot(CaptureInvoiceSnapshot(entity)),
            entity => DeleteInvoiceAsync(db, entity, cancellationToken),
            (entity, snapshot) => RestoreInvoiceSnapshotAsync(db, entity, snapshot, cancellationToken));
    }

    private async Task<bool> RollbackActAsync(PaymentContext db, SpreadsheetImportBatchOperation operation, CancellationToken cancellationToken)
    {
        return await RollbackEntityAsync<Act, ActSnapshot>(
            db,
            operation,
            cancellationToken,
            async id => await db.Acts
                .IgnoreQueryFilters()
                .Include(item => item.LineItems)
                .FirstOrDefaultAsync(item => item.Id == id, cancellationToken),
            entity => SerializeSnapshot(CaptureActSnapshot(entity)),
            entity =>
            {
                if (entity.LineItems.Count > 0)
                {
                    db.RemoveRange(entity.LineItems);
                }

                db.Remove(entity);
                return Task.CompletedTask;
            },
            (entity, snapshot) =>
            {
                RestoreActSnapshot(db, entity, snapshot);
                return Task.CompletedTask;
            });
    }

    private async Task<bool> RollbackEntityAsync<TEntity, TSnapshot>(
        PaymentContext db,
        SpreadsheetImportBatchOperation operation,
        CancellationToken cancellationToken,
        Func<int, Task<TEntity?>> loader,
        Func<TEntity, string> currentSnapshotFactory,
        Func<TEntity, Task> deleteAction,
        Func<TEntity, TSnapshot, Task> restoreAction)
        where TEntity : class
    {
        if (!operation.EntityId.HasValue)
        {
            operation.RollbackAction = SpreadsheetImportRollbackAction.Error;
            operation.RollbackMessage = "Для операции не сохранен идентификатор сущности.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var entity = await loader(operation.EntityId.Value);
        if (entity is null)
        {
            operation.RollbackAction = SpreadsheetImportRollbackAction.Skipped;
            operation.RollbackMessage = "Сущность уже отсутствует в базе.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var currentJson = currentSnapshotFactory(entity);
        if (!string.Equals(currentJson, operation.AfterJson, StringComparison.Ordinal))
        {
            operation.RollbackAction = SpreadsheetImportRollbackAction.Skipped;
            operation.RollbackMessage = "Откат пропущен: запись была изменена после импорта.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        try
        {
            if (operation.Action == SpreadsheetImportOperationAction.Created)
            {
                await deleteAction(entity);
                await db.SaveChangesAsync(cancellationToken);
                operation.RollbackAction = SpreadsheetImportRollbackAction.Deleted;
                operation.RollbackMessage = "Созданная импортом запись удалена.";
                operation.RolledBackAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var snapshot = DeserializeSnapshot<TSnapshot>(operation.BeforeJson);
            if (snapshot is null)
            {
                operation.RollbackAction = SpreadsheetImportRollbackAction.Error;
                operation.RollbackMessage = "Не удалось восстановить исходный снапшот записи.";
                operation.RolledBackAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            await restoreAction(entity, snapshot);
            await db.SaveChangesAsync(cancellationToken);
            operation.RollbackAction = SpreadsheetImportRollbackAction.Restored;
            operation.RollbackMessage = "Запись восстановлена к состоянию до импорта.";
            operation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spreadsheet rollback failed for operation {OperationId}", operation.Id);
            db.ChangeTracker.Clear();

            var storedOperation = await db.SpreadsheetImportBatchOperations.FirstAsync(item => item.Id == operation.Id, cancellationToken);
            storedOperation.RollbackAction = SpreadsheetImportRollbackAction.Error;
            storedOperation.RollbackMessage = $"Не удалось выполнить откат: {ex.Message}";
            storedOperation.RolledBackAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
    }

    private static void RestoreClientSnapshot(Client client, ClientSnapshot snapshot)
    {
        client.Name = snapshot.Name;
        client.Email = snapshot.Email;
        client.Phone = snapshot.Phone;
        client.Company = snapshot.Company;
        client.Address = snapshot.Address;
        client.Notes = snapshot.Notes;
        client.IsActive = snapshot.IsActive;
        client.LegalEntityId = snapshot.LegalEntityId;
        client.ResponsibleUserId = snapshot.ResponsibleUserId;
        client.ClientStatusId = snapshot.ClientStatusId;
    }
}
