using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private async Task ApplyActAsync(
        PaymentContext db,
        int batchId,
        ActDraft draft,
        ImportState state,
        CancellationToken cancellationToken)
    {
        var sourceKeys = BuildActMatchKeys(draft.ExternalId, draft.Number, draft.Date, draft.Type);
        var sourceKey = BuildActNaturalKey(draft.Number, draft.Date, draft.Type);
        var sourceTitle = BuildActTitle(draft.Number, draft.Date);
        if (string.IsNullOrWhiteSpace(sourceKey) || !draft.Amount.HasValue || string.IsNullOrWhiteSpace(draft.CounterpartyName))
        {
            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "acts", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle, SpreadsheetImportOperationAction.Error, "Act", null,
                "Недостаточно данных для импорта акта."));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var clientId = FindClientId(state, draft.CounterpartyName, draft.CounterpartyInn);
            if (!clientId.HasValue)
            {
                db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                    batchId, "acts", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                    sourceKey, sourceTitle, SpreadsheetImportOperationAction.Error, "Act", null,
                    "Не удалось сопоставить контрагента акта. Проверьте, что нужный контрагент уже есть в базе или выбран в разделе импорта контрагентов."));
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var existingActId = FindExistingActStateItem(state, sourceKeys)?.ActId ?? 0;

            var act = existingActId > 0
                ? await db.Acts.Include(item => item.LineItems).FirstOrDefaultAsync(item => item.Id == existingActId, cancellationToken)
                : null;

            var beforeJson = act is null ? null : SerializeSnapshot(CaptureActSnapshot(act));
            act ??= new Act();
            if (act.Id == 0)
            {
                db.Acts.Add(act);
            }

            var responsibleId = FindUserId(state.Users, draft.ResponsibleName);
            var invoicePayment = await FindInvoicePaymentForActAsync(db, draft.InvoiceExternalId, draft.InvoiceNumber, draft.InvoiceDate, draft.Type, cancellationToken);
            ApplyActValues(act, draft, clientId.Value, responsibleId, invoicePayment);
            SyncActLineItems(db, act, invoicePayment, BuildEffectiveLineItems(draft.ParsedLineItems, draft.Amount.Value, draft.Nomenclature, sourceTitle));

            var provisionalAfterJson = SerializeSnapshot(CaptureActSnapshot(act));
            if (beforeJson is not null && string.Equals(beforeJson, provisionalAfterJson, StringComparison.Ordinal))
            {
                db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                    batchId, "acts", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                    sourceKey, sourceTitle, SpreadsheetImportOperationAction.Skipped, "Act", act.Id,
                    "Изменений не требуется.", beforeJson, provisionalAfterJson,
                    JsonSerializer.Serialize(new { draft.ParseWarnings }, JsonOptions)));
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            await db.SaveChangesAsync(cancellationToken);
            var actId = act.Id;
            db.ChangeTracker.Clear();
            var persistedAct = await LoadActForSnapshotAsync(db, actId, cancellationToken);
            var afterJson = SerializeSnapshot(CaptureActSnapshot(persistedAct));
            var operationAction = beforeJson is null
                ? SpreadsheetImportOperationAction.Created
                : string.Equals(beforeJson, afterJson, StringComparison.Ordinal)
                    ? SpreadsheetImportOperationAction.Skipped
                    : SpreadsheetImportOperationAction.Updated;
            state.UpsertAct(new ActStateItem
            {
                ActId = actId,
                Key = sourceKey,
                ExternalId = NormalizeOneCExternalId(draft.ExternalId),
                Number = NormalizeText(draft.Number),
                Date = draft.Date.HasValue ? ActDomainService.NormalizeLocalDate(draft.Date.Value) : null,
                Amount = draft.Amount ?? 0m,
                CounterpartyName = NormalizeText(draft.CounterpartyName),
                CounterpartyInn = NormalizeInn(draft.CounterpartyInn),
                Status = MapActStatus(draft.StatusText),
                Type = draft.Type,
                InvoiceNumber = NormalizeText(draft.InvoiceNumber),
                ResponsibleName = NormalizeText(draft.ResponsibleName),
                ImportedInCurrentBatch = true,
            });

            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "acts", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle, operationAction,
                "Act", actId,
                operationAction == SpreadsheetImportOperationAction.Created
                    ? "Акт создан."
                    : operationAction == SpreadsheetImportOperationAction.Skipped
                        ? "Изменений не требуется."
                        : "Акт обновлен.",
                beforeJson,
                afterJson,
                JsonSerializer.Serialize(new { draft.ParseWarnings }, JsonOptions)));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spreadsheet act import failed for {FileName}:{RowNumber}", draft.FileName, draft.RowNumber);
            db.ChangeTracker.Clear();
            db.SpreadsheetImportBatchOperations.Add(CreateOperation(
                batchId, "acts", draft.FileName, draft.FileHash, draft.PreviewId, draft.RowNumber,
                sourceKey, sourceTitle, SpreadsheetImportOperationAction.Error, "Act", null, ex.Message,
                metadata: JsonSerializer.Serialize(new { draft.ParseWarnings }, JsonOptions)));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<Act> LoadActForSnapshotAsync(PaymentContext db, int actId, CancellationToken cancellationToken)
        => await db.Acts
            .Include(item => item.LineItems)
            .FirstAsync(item => item.Id == actId, cancellationToken);

    private static SpreadsheetImportBatchOperation CreateOperation(
        int batchId,
        string sectionKey,
        string sourceFileName,
        string sourceFileHash,
        string sourceRowId,
        int sourceRowNumber,
        string? sourceKey,
        string sourceTitle,
        string action,
        string entityType,
        int? entityId,
        string? message,
        string? beforeJson = null,
        string? afterJson = null,
        string? metadata = null)
        => new()
        {
            BatchId = batchId,
            SectionKey = sectionKey,
            SourceFileName = sourceFileName,
            SourceFileHash = sourceFileHash,
            SourceRowId = sourceRowId,
            SourceRowNumber = sourceRowNumber,
            SourceKey = sourceKey ?? string.Empty,
            SourceTitle = sourceTitle,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Message = message,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            MetadataJson = metadata,
            CreatedAt = DateTime.UtcNow,
        };

    private static void ApplyCounterpartyValues(Client client, CounterpartyDraft draft, int? responsibleId, string importMode)
    {
        var clientName = BuildCounterpartyClientName(draft, importMode);
        var clientCompany = BuildCounterpartyClientCompany(draft, importMode);

        if (string.IsNullOrWhiteSpace(client.Name))
        {
            client.Name = Truncate(clientName, 200);
        }

        if (string.IsNullOrWhiteSpace(client.Company) && !string.IsNullOrWhiteSpace(clientCompany))
        {
            client.Company = Truncate(clientCompany, 200);
        }

        if (string.IsNullOrWhiteSpace(client.Phone) && !string.IsNullOrWhiteSpace(draft.Phone))
        {
            client.Phone = Truncate(draft.Phone, 50);
        }

        if (string.IsNullOrWhiteSpace(client.Email) && !string.IsNullOrWhiteSpace(draft.Email))
        {
            client.Email = Truncate(draft.Email, 200);
        }

        if (string.IsNullOrWhiteSpace(client.Address) && !string.IsNullOrWhiteSpace(draft.Address))
        {
            client.Address = Truncate(draft.Address, 500);
        }

        client.Notes = MergeClientNotes(client.Notes, draft);
        if (!client.ResponsibleUserId.HasValue && responsibleId.HasValue)
        {
            client.ResponsibleUserId = responsibleId;
        }

        client.IsActive = true;
    }

    private async Task<Invoice> ResolveInvoiceEntityAsync(PaymentContext db, Payment payment, InvoiceDraft draft, CancellationToken cancellationToken)
    {
        if (payment.Invoice is not null)
        {
            payment.Invoice.Number = Truncate(draft.Number, 120);
            payment.Invoice.Date = draft.Date!.Value.Date;
            return payment.Invoice;
        }

        var number = Truncate(draft.Number, 120);
        var date = draft.Date!.Value.Date;
        var invoice = await db.Invoices.FirstOrDefaultAsync(item => item.Number == number && item.Date == date, cancellationToken);
        if (invoice is null)
        {
            invoice = new Invoice { Number = number, Date = date };
            db.Invoices.Add(invoice);
        }

        payment.Invoice = invoice;
        payment.InvoiceId = invoice.Id > 0 ? invoice.Id : null;
        return invoice;
    }

    private void ApplyInvoiceValues(
        Payment payment,
        InvoiceDraft draft,
        int clientId,
        int? responsibleId,
        Invoice invoiceEntity,
        DateTime localToday)
    {
        var amount = Math.Max(0m, draft.Amount ?? 0m);
        var dueDate = (draft.DueDate ?? draft.Date ?? localToday).Date;
        var invoiceDate = (draft.Date ?? draft.DueDate ?? localToday).Date;
        var status = MapPaymentStatus(draft.StatusText, draft.DueDate ?? draft.Date, draft.PaidDate, localToday);

        payment.DocumentKind = DocumentKind.Invoice;
        payment.Type = draft.Type;

        payment.Invoice = invoiceEntity;
        payment.InvoiceId = invoiceEntity.Id > 0 ? invoiceEntity.Id : null;
        payment.Account = Truncate(draft.Number, 120);
        payment.AccountDate = invoiceDate;
        payment.LegacyAccountNumber = payment.Account;
        payment.LegacyAccountDate = invoiceDate;
        payment.Amount = amount;
        payment.Date = dueDate;
        payment.PlannedDate = dueDate;
        payment.Description = Truncate(BuildInvoiceDescription(draft), 500);
        payment.Status = status;
        payment.IsPaid = status == PaymentStatus.Completed;
        payment.PaidAmount = CalculatePaidAmount(status, amount, draft.PaidDate, draft.StatusText);
        payment.PaidDate = payment.PaidAmount > 0m ? draft.PaidDate ?? payment.PaidDate ?? invoiceDate : null;
        payment.LastPaymentDate = payment.PaidDate;
        payment.ClientId = clientId;
        if (payment.Type != PaymentType.Expense)
        {
            payment.ExpenseClientId = null;
        }

        payment.CounterpartyName = Truncate(draft.CounterpartyName, 200);
        payment.Notes = MergeTextValues(payment.Notes, BuildInvoiceImportNotes(draft));
        payment.SystemNotes = Truncate(
            MergeImportMarker(
                InvoiceLineItemStorage.MergeIntoSystemNotes(payment.SystemNotes, BuildInvoiceLineItemRequests(
                    BuildEffectiveLineItems(draft.ParsedLineItems, amount, draft.Nomenclature, BuildInvoiceTitle(draft.Number, draft.Date)))),
                "invoices",
                draft.FileName,
                draft.RowNumber),
            4000);

        if (responsibleId.HasValue && payment.Responsibles.All(item => item.UserId != responsibleId.Value))
        {
            payment.Responsibles.Add(new PaymentResponsible { UserId = responsibleId.Value });
        }
    }

    private static List<InvoiceLineItemRequest> BuildInvoiceLineItemRequests(IEnumerable<ImportedLineItem> items)
        => items.Select(item => new InvoiceLineItemRequest
        {
            Name = item.Name,
            Quantity = item.Quantity,
            Unit = item.Unit,
            Price = item.Price,
        }).ToList();
}
