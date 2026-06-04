using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private async Task ApplyCounterpartyAsync(
        PaymentContext db,
        int batchId,
        CounterpartyDraft draft,
        string importMode,
        ImportState state,
        int? fallbackUserId,
        CancellationToken cancellationToken)
    {
        var sourceKey = BuildCounterpartyKey(draft.Name, draft.Inn, draft.Phone, draft.Email);
        var sourceTitle = draft.Name;
        if (string.IsNullOrWhiteSpace(sourceTitle) || string.IsNullOrWhiteSpace(sourceKey))
        {
            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "counterparties", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle ?? "Контрагент", SpreadsheetImportOperationAction.Error, "Client", null,
                "Не удалось определить уникальный ключ контрагента.",
                metadata: JsonSerializer.Serialize(new CounterpartyOperationMetadata
                {
                    Derived = draft.Derived,
                    ImportMode = NormalizeCounterpartyImportMode(importMode),
                }, JsonOptions)));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var normalizedImportMode = NormalizeCounterpartyImportMode(importMode);
            var existingClientState = FindClient(state, draft.Name, draft.Inn, draft.Phone, draft.Email);
            var existingLegalEntityState = normalizedImportMode == CounterpartyImportModeLegalEntityWithClient
                ? FindLegalEntity(state, draft.Name, draft.Inn, draft.Phone, draft.Email, existingClientState?.LegalEntityId)
                : null;
            var responsibleId = FindUserId(state.Users, draft.ResponsibleName ?? draft.AutoInvoiceResponsibleName) ?? fallbackUserId;
            var now = DateTime.UtcNow;

            var client = existingClientState is not null
                ? await db.Clients.FirstOrDefaultAsync(item => item.Id == existingClientState.Id, cancellationToken)
                : null;
            var legalEntity = existingClientState?.LegalEntityId.HasValue == true
                ? await db.LegalEntities.FirstOrDefaultAsync(item => item.Id == existingClientState.LegalEntityId.Value, cancellationToken)
                : null;

            if (legalEntity is null
                && existingLegalEntityState is not null)
            {
                legalEntity = await db.LegalEntities.FirstOrDefaultAsync(item => item.Id == existingLegalEntityState.Id, cancellationToken);
            }

            var beforeSnapshot = CaptureCounterpartySnapshot(client, legalEntity);
            var legalEntityCreated = false;

            if (normalizedImportMode == CounterpartyImportModeLegalEntityWithClient && legalEntity is null)
            {
                legalEntity = new LegalEntity
                {
                    ShortName = Truncate(draft.Name, 200),
                    FullName = Truncate(draft.Name, 500),
                    Inn = Truncate(NormalizeInn(draft.Inn), 20),
                    Kpp = Truncate(draft.Kpp, 20),
                    Ogrn = Truncate(draft.Ogrn, 20),
                    Address = Truncate(draft.Address, 500),
                    Phone = Truncate(draft.Phone, 100),
                    Email = Truncate(draft.Email, 200),
                    Director = Truncate(BuildCounterpartyLegalEntityDirector(draft), 200),
                    ResponsibleUserId = responsibleId,
                    OrganizationForm = Truncate(BuildCounterpartyOrganizationForm(draft), 64),
                    BankAccountsJson = MergeLegalEntityBankAccounts(null, draft),
                    Notes = MergeLegalEntityNotes(null, draft),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                db.LegalEntities.Add(legalEntity);
                legalEntityCreated = true;
            }

            if (legalEntity is not null)
            {
                if (string.IsNullOrWhiteSpace(legalEntity.ShortName))
                {
                    legalEntity.ShortName = Truncate(draft.Name, 200);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.FullName))
                {
                    legalEntity.FullName = Truncate(draft.Name, 500);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.Inn) && !string.IsNullOrWhiteSpace(draft.Inn))
                {
                    legalEntity.Inn = Truncate(NormalizeInn(draft.Inn), 20);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.Kpp) && !string.IsNullOrWhiteSpace(draft.Kpp))
                {
                    legalEntity.Kpp = Truncate(draft.Kpp, 20);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.Ogrn) && !string.IsNullOrWhiteSpace(draft.Ogrn))
                {
                    legalEntity.Ogrn = Truncate(draft.Ogrn, 20);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.Address) && !string.IsNullOrWhiteSpace(draft.Address))
                {
                    legalEntity.Address = Truncate(draft.Address, 500);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.Phone) && !string.IsNullOrWhiteSpace(draft.Phone))
                {
                    legalEntity.Phone = Truncate(draft.Phone, 100);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.Email) && !string.IsNullOrWhiteSpace(draft.Email))
                {
                    legalEntity.Email = Truncate(draft.Email, 200);
                }

                if (string.IsNullOrWhiteSpace(legalEntity.Director))
                {
                    legalEntity.Director = Truncate(BuildCounterpartyLegalEntityDirector(draft), 200);
                }

                if (!legalEntity.ResponsibleUserId.HasValue && responsibleId.HasValue)
                {
                    legalEntity.ResponsibleUserId = responsibleId;
                }

                if (string.IsNullOrWhiteSpace(legalEntity.OrganizationForm))
                {
                    legalEntity.OrganizationForm = Truncate(BuildCounterpartyOrganizationForm(draft), 64);
                }

                legalEntity.BankAccountsJson = MergeLegalEntityBankAccounts(legalEntity.BankAccountsJson, draft);
                legalEntity.Notes = MergeLegalEntityNotes(legalEntity.Notes, draft);
                legalEntity.UpdatedAt = now;
            }

            var clientCreated = client is null;
            client ??= new Client
            {
                Name = Truncate(BuildCounterpartyClientName(draft, normalizedImportMode), 200),
                Company = Truncate(BuildCounterpartyClientCompany(draft, normalizedImportMode), 200),
                Phone = Truncate(draft.Phone, 50),
                Email = Truncate(draft.Email, 200),
                Notes = MergeClientNotes(null, draft),
                IsActive = true,
                ResponsibleUserId = responsibleId,
            };

            if (client.Id == 0)
            {
                db.Clients.Add(client);
            }

            ApplyCounterpartyValues(client, draft, responsibleId, normalizedImportMode);

            if (normalizedImportMode == CounterpartyImportModeLegalEntityWithClient && legalEntity is not null)
            {
                client.LegalEntity = legalEntity;
                client.LegalEntityId = legalEntity.Id > 0 ? legalEntity.Id : null;
                client.Company = Truncate(legalEntity.ShortName, 200);
            }

            var provisionalAfterSnapshot = CaptureCounterpartySnapshot(
                client,
                normalizedImportMode == CounterpartyImportModeLegalEntityWithClient ? legalEntity : null);

            if (!clientCreated
                && !legalEntityCreated
                && string.Equals(SerializeSnapshot(beforeSnapshot), SerializeSnapshot(provisionalAfterSnapshot), StringComparison.Ordinal))
            {
                db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                    batchId, "counterparties", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                    sourceKey, sourceTitle, SpreadsheetImportOperationAction.Skipped, "Client", client.Id,
                    "Изменений не требуется.",
                    SerializeSnapshot(beforeSnapshot),
                    SerializeSnapshot(provisionalAfterSnapshot),
                    JsonSerializer.Serialize(new CounterpartyOperationMetadata
                    {
                        Derived = draft.Derived,
                        ImportMode = normalizedImportMode,
                        LegalEntityId = legalEntity?.Id,
                        LegalEntityCreated = false,
                    }, JsonOptions)));
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            await db.SaveChangesAsync(cancellationToken);

            var persistedClientId = client.Id;
            db.ChangeTracker.Clear();

            var persistedClient = await db.Clients
                .Include(item => item.LegalEntity)
                .FirstAsync(item => item.Id == persistedClientId, cancellationToken);
            var persistedLegalEntity = persistedClient.LegalEntityId.HasValue
                ? await db.LegalEntities.FirstOrDefaultAsync(item => item.Id == persistedClient.LegalEntityId.Value, cancellationToken)
                : null;
            var afterSnapshot = CaptureCounterpartySnapshot(persistedClient, persistedLegalEntity);

            state.UpsertClient(MapClientStateItem(persistedClient, persistedLegalEntity));
            if (persistedLegalEntity is not null)
            {
                state.UpsertLegalEntity(MapLegalEntityStateItem(persistedLegalEntity));
            }

            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "counterparties", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle,
                clientCreated ? SpreadsheetImportOperationAction.Created : SpreadsheetImportOperationAction.Updated,
                "Client",
                persistedClient.Id,
                BuildCounterpartyOperationMessage(clientCreated, normalizedImportMode, legalEntityCreated, persistedLegalEntity is not null),
                SerializeSnapshot(beforeSnapshot),
                SerializeSnapshot(afterSnapshot),
                JsonSerializer.Serialize(new CounterpartyOperationMetadata
                {
                    Derived = draft.Derived,
                    ImportMode = normalizedImportMode,
                    LegalEntityId = persistedLegalEntity?.Id,
                    LegalEntityCreated = legalEntityCreated,
                }, JsonOptions)));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spreadsheet counterparty import failed for {FileName}:{RowNumber}", draft.FileName, draft.RowNumber);
            db.ChangeTracker.Clear();
            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "counterparties", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle ?? "Контрагент", SpreadsheetImportOperationAction.Error, "Client", null, ex.Message,
                metadata: JsonSerializer.Serialize(new CounterpartyOperationMetadata
                {
                    Derived = draft.Derived,
                    ImportMode = NormalizeCounterpartyImportMode(importMode),
                }, JsonOptions)));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string BuildCounterpartyOperationMessage(
        bool clientCreated,
        string importMode,
        bool legalEntityCreated,
        bool hasLegalEntity)
    {
        if (importMode != CounterpartyImportModeLegalEntityWithClient)
        {
            return clientCreated
                ? "Контрагент создан."
                : "Контрагент обновлен.";
        }

        if (clientCreated && legalEntityCreated)
        {
            return "Созданы клиент и связанное юридическое лицо.";
        }

        if (clientCreated && hasLegalEntity)
        {
            return "Создан клиент и привязан к юридическому лицу.";
        }

        if (!clientCreated && legalEntityCreated)
        {
            return "Контрагент обновлен и дополнен новым юридическим лицом.";
        }

        return "Контрагент и связанное юридическое лицо обновлены.";
    }

    private static ClientStateItem MapClientStateItem(Client client, LegalEntity? legalEntity)
    {
        return new ClientStateItem
        {
            Id = client.Id,
            Name = client.Name,
            NormalizedName = NormalizeNameKey(client.Name),
            Company = NormalizeText(client.Company),
            NormalizedCompany = NormalizeNameKey(client.Company),
            NormalizedInn = NormalizeInn(legalEntity?.Inn) ?? ExtractClientInn(client.Notes),
            NormalizedPhone = NormalizePhone(client.Phone),
            NormalizedEmail = NormalizeEmail(client.Email),
            Phone = NormalizeText(client.Phone),
            Email = NormalizeEmail(client.Email),
            Address = NormalizeText(client.Address),
            ResponsibleName = null,
            LegalEntityId = client.LegalEntityId,
            LegalEntityShortName = NormalizeText(legalEntity?.ShortName),
            LegalEntityFullName = NormalizeText(legalEntity?.FullName),
            MatchKeys = BuildCounterpartyMatchKeys(
                client.Name,
                client.Company,
                legalEntity?.ShortName,
                legalEntity?.FullName),
        };
    }

    private static LegalEntityStateItem MapLegalEntityStateItem(LegalEntity entity)
    {
        return new LegalEntityStateItem
        {
            Id = entity.Id,
            ShortName = entity.ShortName,
            FullName = NormalizeText(entity.FullName),
            Inn = NormalizeInn(entity.Inn),
            Kpp = NormalizeText(entity.Kpp),
            Ogrn = NormalizeText(entity.Ogrn),
            Address = NormalizeText(entity.Address),
            Phone = NormalizeText(entity.Phone),
            Email = NormalizeEmail(entity.Email),
            Director = NormalizeText(entity.Director),
            ResponsibleName = null,
            OrganizationForm = NormalizeText(entity.OrganizationForm),
            BankAccountsJson = NormalizeText(entity.BankAccountsJson),
            NormalizedShortName = NormalizeNameKey(entity.ShortName),
            NormalizedFullName = NormalizeNameKey(entity.FullName),
            NormalizedInn = NormalizeInn(entity.Inn),
            NormalizedPhone = NormalizePhone(entity.Phone),
            NormalizedEmail = NormalizeEmail(entity.Email),
            MatchKeys = BuildCounterpartyMatchKeys(entity.ShortName, entity.FullName),
        };
    }

    private async Task ApplyInvoiceAsync(
        PaymentContext db,
        int batchId,
        InvoiceDraft draft,
        ImportState state,
        DateTime localToday,
        CancellationToken cancellationToken)
    {
        var sourceKeys = BuildInvoiceMatchKeys(draft.ExternalId, draft.Number, draft.Date, draft.Type);
        var sourceKey = BuildInvoiceNaturalKey(draft.Number, draft.Date, draft.Type);
        var sourceTitle = BuildInvoiceTitle(draft.Number, draft.Date);
        if (string.IsNullOrWhiteSpace(sourceKey) || !draft.Amount.HasValue || string.IsNullOrWhiteSpace(draft.CounterpartyName))
        {
            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "invoices", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle, SpreadsheetImportOperationAction.Error, "Payment", null,
                "Недостаточно данных для импорта счета."));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var clientId = FindClientId(state, draft.CounterpartyName, draft.CounterpartyInn);
            if (!clientId.HasValue)
            {
                db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                    batchId, "invoices", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                    sourceKey, sourceTitle, SpreadsheetImportOperationAction.Error, "Payment", null,
                    "Не удалось сопоставить контрагента счета. Проверьте, что нужный контрагент уже есть в базе или выбран в разделе импорта контрагентов."));
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var responsibleId = FindUserId(state.Users, draft.ResponsibleName ?? draft.CreatedByName);
            var existingPaymentId = FindExistingInvoiceStateItem(state, sourceKeys)?.PaymentId ?? 0;

            var payment = existingPaymentId > 0
                ? await db.Payments.Include(item => item.Invoice).Include(item => item.InvoiceLineItems).Include(item => item.Responsibles)
                    .FirstOrDefaultAsync(item => item.Id == existingPaymentId, cancellationToken)
                : null;

            var beforeJson = payment is null ? null : SerializeSnapshot(CaptureInvoiceSnapshot(payment));
            payment ??= new Payment { DocumentKind = DocumentKind.Invoice, Type = draft.Type };
            if (payment.Id == 0)
            {
                db.Payments.Add(payment);
            }

            var invoiceEntity = await ResolveInvoiceEntityAsync(db, payment, draft, cancellationToken);
            ApplyInvoiceValues(payment, draft, clientId.Value, responsibleId, invoiceEntity, localToday);
            SyncInvoiceLineItems(db, payment, BuildEffectiveLineItems(draft.ParsedLineItems, draft.Amount.Value, draft.Nomenclature, sourceTitle));
            SyncPaymentResponsibles(payment, responsibleId);

            var provisionalAfterJson = SerializeSnapshot(CaptureInvoiceSnapshot(payment));
            if (beforeJson is not null && string.Equals(beforeJson, provisionalAfterJson, StringComparison.Ordinal))
            {
                db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                    batchId, "invoices", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                    sourceKey, sourceTitle, SpreadsheetImportOperationAction.Skipped, "Payment", payment.Id,
                    "Изменений не требуется.", beforeJson, provisionalAfterJson,
                    JsonSerializer.Serialize(new { draft.ParseWarnings }, JsonOptions)));
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            await db.SaveChangesAsync(cancellationToken);
            var paymentId = payment.Id;
            db.ChangeTracker.Clear();
            var persistedPayment = await LoadInvoicePaymentForSnapshotAsync(db, paymentId, cancellationToken);
            var afterJson = SerializeSnapshot(CaptureInvoiceSnapshot(persistedPayment));
            var operationAction = beforeJson is null
                ? SpreadsheetImportOperationAction.Created
                : string.Equals(beforeJson, afterJson, StringComparison.Ordinal)
                    ? SpreadsheetImportOperationAction.Skipped
                    : SpreadsheetImportOperationAction.Updated;
            state.UpsertInvoice(new InvoiceStateItem
            {
                PaymentId = paymentId,
                Key = sourceKey,
                ExternalId = NormalizeOneCExternalId(draft.ExternalId),
                Number = NormalizeText(draft.Number),
                Date = draft.Date?.Date,
                Amount = draft.Amount ?? 0m,
                DueDate = draft.DueDate?.Date,
                CounterpartyName = NormalizeText(draft.CounterpartyName),
                Status = MapPaymentStatus(draft.StatusText, draft.DueDate ?? draft.Date, draft.PaidDate, localToday),
                Type = draft.Type,
                PaidDate = draft.PaidDate?.Date,
                ActReference = NormalizeText(draft.ActReference),
                ResponsibleName = NormalizeText(draft.ResponsibleName ?? draft.CreatedByName),
                ImportedInCurrentBatch = true,
            });

            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "invoices", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle, operationAction,
                "Payment",
                paymentId,
                operationAction == SpreadsheetImportOperationAction.Created
                    ? "Счет создан как платеж и привязан к карточке счета."
                    : operationAction == SpreadsheetImportOperationAction.Skipped
                        ? "Изменений не требуется."
                        : "Счет обновлен как платеж с сохранением связи с карточкой счета.",
                beforeJson,
                afterJson,
                JsonSerializer.Serialize(new { draft.ParseWarnings }, JsonOptions)));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spreadsheet invoice import failed for {FileName}:{RowNumber}", draft.FileName, draft.RowNumber);
            db.ChangeTracker.Clear();
            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "invoices", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle, SpreadsheetImportOperationAction.Error, "Payment", null, ex.Message,
                metadata: JsonSerializer.Serialize(new { draft.ParseWarnings }, JsonOptions)));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<Payment> LoadInvoicePaymentForSnapshotAsync(PaymentContext db, int paymentId, CancellationToken cancellationToken)
        => await db.Payments
            .Include(item => item.Invoice)
            .Include(item => item.InvoiceLineItems)
            .Include(item => item.Responsibles)
            .FirstAsync(item => item.Id == paymentId, cancellationToken);
}
