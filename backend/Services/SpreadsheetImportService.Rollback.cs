using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private async Task<bool> RollbackCounterpartyAsync(PaymentContext db, SpreadsheetImportBatchOperation operation, CancellationToken cancellationToken)
    {
        await db.Entry(operation).ReloadAsync(cancellationToken);
        if (CounterpartyRollbackCompleted(operation)) return true;
        IDbContextTransaction? transaction = null;
        bool? outcome = null;
        var commitStarted = false;
        string? failureMessage = null;
        DateTime? failureAt = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var companyId = operation.EntityId.HasValue
                ? await db.Clients.IgnoreQueryFilters().AsNoTracking().Where(item => item.Id == operation.EntityId.Value)
                    .Select(item => item.LegalEntityId).SingleOrDefaultAsync(cancellationToken)
                : null;
            companyId ??= DeserializeSnapshot<CounterpartyOperationMetadata>(operation.MetadataJson)?.LegalEntityId;
            // Retain archived history while serializing against financial writers.
            // A missing historical company can still be restored by the existing snapshot rules.
            if (companyId.HasValue)
                await db.LegalEntities.IgnoreQueryFilters().Where(item => item.Id == companyId.Value)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ShortName, item => item.ShortName), cancellationToken);
            if (await db.SpreadsheetImportBatchOperations.Where(item => item.Id == operation.Id && item.BatchId == operation.BatchId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SourceTitle, item => item.SourceTitle), cancellationToken) != 1)
                throw new InvalidOperationException("Запись импорта больше недоступна.");
            await db.Entry(operation).ReloadAsync(cancellationToken);
            if (CounterpartyRollbackCompleted(operation)) outcome = true;
            else
            {
                if (operation.EntityId.HasValue)
                    await db.Clients.IgnoreQueryFilters().Where(item => item.Id == operation.EntityId.Value)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Name, item => item.Name), cancellationToken);
                var success = await RollbackCounterpartyCoreAsync(db, operation, cancellationToken);
                var stored = await db.SpreadsheetImportBatchOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operation.Id, cancellationToken);
                if (!success && stored.RollbackAction == SpreadsheetImportRollbackAction.Error)
                {
                    failureMessage = stored.RollbackMessage;
                    failureAt = stored.RolledBackAt;
                    await transaction.RollbackAsync(cancellationToken);
                }
                else outcome = success;
            }
            if (outcome.HasValue)
            {
                commitStarted = true;
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Atomic counterparty rollback failed for operation {OperationId}", operation.Id);
            failureMessage ??= $"Не удалось выполнить откат: {ex.Message}";
            if (transaction is not null)
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await transaction.RollbackAsync(rollbackTimeout.Token); }
                catch (Exception rollbackError) { ex.Data["CounterpartyRollbackFailure"] = rollbackError; }
            }
            outcome = null;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
        if (outcome.HasValue) return outcome.Value;
        cancellationToken.ThrowIfCancellationRequested();
        db.ChangeTracker.Clear();
        var actual = await db.SpreadsheetImportBatchOperations.SingleAsync(item => item.Id == operation.Id, cancellationToken);
        // A lost commit acknowledgement must not overwrite the durable receipt.
        if (commitStarted && CounterpartyRollbackCompleted(actual)) return true;
        if (commitStarted && actual.RollbackAction == SpreadsheetImportRollbackAction.Skipped) return false;
        actual.RollbackAction = SpreadsheetImportRollbackAction.Error;
        actual.RollbackMessage = failureMessage ?? "Не удалось выполнить откат контрагента.";
        actual.RolledBackAt = failureAt ?? DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return false;
    }

    private static bool CounterpartyRollbackCompleted(SpreadsheetImportBatchOperation operation)
        => operation.RollbackAction is SpreadsheetImportRollbackAction.Deleted or SpreadsheetImportRollbackAction.Restored;

    private async Task<bool> RollbackCounterpartyCoreAsync(PaymentContext db, SpreadsheetImportBatchOperation operation, CancellationToken cancellationToken)
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
                    if (storedLegalEntity is not null && !await IsLegalEntityStillReferencedAsync(db, storedLegalEntity.Id, client.Id, cancellationToken))
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
                    if (storedLegalEntity is not null && !await IsLegalEntityStillReferencedAsync(db, storedLegalEntity.Id, client.Id, cancellationToken))
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
        // Rollback must retain referenced history even when a related document is
        // archived. Evaluate the indexed references in one query without loading it.
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(company => company.Id == legalEntityId && (
                db.Clients.Any(item => item.LegalEntityId == legalEntityId && (!excludeClientId.HasValue || item.Id != excludeClientId.Value))
                || db.ExternalContacts.Any(item => item.LegalEntityId == legalEntityId || item.LinkedLegalEntityId == legalEntityId)
                || db.AddressRecords.Any(item => item.LegalEntityId == legalEntityId)
                || db.UserCompanyAccesses.Any(item => item.LegalEntityId == legalEntityId)
                || db.MoneyAccounts.Any(item => item.CompanyId == legalEntityId)
                || db.BankStatementImportBatches.Any(item => item.CompanyId == legalEntityId)
                || db.BankConnections.Any(item => item.LegalEntityId == legalEntityId)
                || db.CompanyBranches.Any(item => item.LegalEntityId == legalEntityId)
                || db.Payments.Any(item => item.SellerCompanyId == legalEntityId || item.LegalEntityId == legalEntityId || item.ExpenseLegalEntityId == legalEntityId)
                || db.Acts.Any(item => item.SellerCompanyId == legalEntityId || item.LegalEntityId == legalEntityId)
                || db.Contracts.Any(item => item.SellerCompanyId == legalEntityId || item.LegalEntities.Any(party => party.Id == legalEntityId))),
                cancellationToken)) return true;

        // Registration as an own company is also a use, before the first payment.
        return (await OwnCompanyRegistry.LoadIdsAsync(db, cancellationToken)).Contains(legalEntityId);
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
            entity => DeleteInvoiceAsync(db, entity, operation.BatchId, cancellationToken),
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
            async (entity, snapshot) =>
            {
                if (snapshot.Act.InvoiceId.HasValue && !await db.Payments.IgnoreQueryFilters()
                    .ApplyUnambiguousInvoiceLinks().AnyAsync(payment => payment.Id == snapshot.Act.InvoiceId.Value
                        && payment.ClientId == snapshot.Act.ClientId, cancellationToken))
                    throw new InvalidOperationException(InvoiceQueryExtensions.ConflictMessage);
                RestoreActSnapshot(db, entity, snapshot);
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
