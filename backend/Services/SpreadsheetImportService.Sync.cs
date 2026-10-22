using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private static void SyncPaymentResponsibles(Payment payment, int? responsibleId)
    {
        if (!responsibleId.HasValue || payment.Responsibles.Any(item => item.UserId == responsibleId.Value))
        {
            return;
        }

        payment.Responsibles.Add(new PaymentResponsible
        {
            UserId = responsibleId.Value,
        });
    }

    private static void SyncInvoiceLineItems(PaymentContext db, Payment payment, IReadOnlyCollection<ImportedLineItem> items)
    {
        var existing = payment.InvoiceLineItems.OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToList();
        var desired = items.ToList();

        for (var index = 0; index < desired.Count; index++)
        {
            var desiredItem = desired[index];
            var lineItem = index < existing.Count
                ? existing[index]
                : new InvoiceLineItem { Payment = payment };

            lineItem.SortOrder = index;
            lineItem.Name = Truncate(desiredItem.Name, 2000);
            lineItem.Quantity = desiredItem.Quantity <= 0 ? 1m : desiredItem.Quantity;
            lineItem.Unit = Truncate(desiredItem.Unit, 32);
            lineItem.Price = desiredItem.Price;

            if (lineItem.Id == 0 && !payment.InvoiceLineItems.Contains(lineItem))
            {
                payment.InvoiceLineItems.Add(lineItem);
            }
        }

        if (existing.Count > desired.Count)
        {
            var extras = existing.Skip(desired.Count).ToList();
            if (extras.Count > 0)
            {
                db.RemoveRange(extras);
            }
        }
    }

    private static void ApplyActValues(Act act, ActDraft draft, int clientId, int? responsibleId, Payment? invoicePayment)
    {
        act.Number = Truncate(draft.Number, 100);
        act.Title = Truncate(BuildActTitle(draft.Number, draft.Date), 200);
        act.Date = ActDomainService.NormalizeLocalDate(draft.Date ?? draft.PaymentDate ?? DateTime.UtcNow);
        act.Amount = Math.Max(0m, draft.Amount ?? 0m);
        act.CreationMode = ActCreationMode.Manual;
        act.InvoiceNumber = Truncate(draft.InvoiceNumber ?? invoicePayment?.Account ?? invoicePayment?.Invoice?.Number, 100);
        act.InvoiceId = invoicePayment?.Id;
        act.CounterpartyInn = Truncate(draft.CounterpartyInn, 32);
        act.Status = MapActStatus(draft.StatusText);
        act.ClientId = clientId;
        act.ResponsibleId = responsibleId ?? act.ResponsibleId;
        var importComment = BuildActImportComment(draft);
        if (!string.IsNullOrWhiteSpace(importComment))
        {
            act.Comment = MergeTextValues(act.Comment, importComment);
        }
    }

    private static void SyncActLineItems(PaymentContext db, Act act, Payment? invoicePayment, IReadOnlyCollection<ImportedLineItem> items)
    {
        var existing = act.LineItems.OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToList();
        var desired = items.ToList();
        var invoiceLineItems = invoicePayment?.InvoiceLineItems.OrderBy(item => item.SortOrder).ToList() ?? new List<InvoiceLineItem>();

        for (var index = 0; index < desired.Count; index++)
        {
            var desiredItem = desired[index];
            var linkedInvoiceLine = index < invoiceLineItems.Count ? invoiceLineItems[index] : null;
            var lineItem = index < existing.Count
                ? existing[index]
                : new ActLineItem { Act = act };

            lineItem.SortOrder = index;
            lineItem.InvoiceLineItemId = linkedInvoiceLine?.Id;
            lineItem.SourceSortOrder = index;
            lineItem.SourceKey = linkedInvoiceLine is null
                ? InvoiceLineItemStorage.BuildSourceKey(null, index)
                : InvoiceLineItemStorage.BuildSourceKey(linkedInvoiceLine.Id, index);
            lineItem.Name = Truncate(desiredItem.Name, 2000);
            lineItem.Quantity = desiredItem.Quantity <= 0 ? 1m : desiredItem.Quantity;
            lineItem.Unit = Truncate(desiredItem.Unit, 32);
            lineItem.Price = desiredItem.Price;

            if (lineItem.Id == 0 && !act.LineItems.Contains(lineItem))
            {
                act.LineItems.Add(lineItem);
            }
        }

        if (existing.Count > desired.Count)
        {
            var extras = existing.Skip(desired.Count).ToList();
            if (extras.Count > 0)
            {
                db.RemoveRange(extras);
            }
        }
    }

    private async Task<Payment?> FindInvoicePaymentForActAsync(
        PaymentContext db,
        string? invoiceExternalId,
        string? invoiceNumber,
        DateTime? invoiceDate,
        PaymentType type,
        int clientId,
        CancellationToken cancellationToken)
    {
        var normalizedExternalId = NormalizeOneCExternalId(invoiceExternalId);
        var normalizedNumber = NormalizeDocumentNumber(invoiceNumber);
        if (string.IsNullOrWhiteSpace(normalizedExternalId) && string.IsNullOrWhiteSpace(normalizedNumber))
        {
            return null;
        }

        var candidates = await db.Payments
            .AsNoTracking()
            .Include(item => item.Invoice)
            .Include(item => item.InvoiceLineItems)
            .Where(item => item.DocumentKind == DocumentKind.Invoice && item.Type == type && item.ClientId == clientId)
            .ApplyUnambiguousInvoiceLinks()
            .ToListAsync(cancellationToken);

        var normalizedInvoiceDate = invoiceDate?.Date;
        bool MatchesExternalId(Payment item)
            => string.Equals(ExtractOneCExternalId(item.Notes), normalizedExternalId, StringComparison.Ordinal);

        bool MatchesNumber(Payment item)
            => string.Equals(NormalizeDocumentNumber(item.Invoice?.Number), normalizedNumber, StringComparison.Ordinal)
                || string.Equals(NormalizeDocumentNumber(item.Account), normalizedNumber, StringComparison.Ordinal)
                || string.Equals(NormalizeDocumentNumber(item.LegacyAccountNumber), normalizedNumber, StringComparison.Ordinal);

        bool MatchesDate(Payment item)
            => !normalizedInvoiceDate.HasValue
                || item.Invoice?.Date.Date == normalizedInvoiceDate.Value
                || item.AccountDate?.Date == normalizedInvoiceDate.Value
                || item.LegacyAccountDate?.Date == normalizedInvoiceDate.Value;

        Payment? SingleOrDefaultMatch(IEnumerable<Payment> source)
        {
            var matches = source.Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        if (!string.IsNullOrWhiteSpace(normalizedExternalId) && !string.IsNullOrWhiteSpace(normalizedNumber))
        {
            var byExternalIdAndNumber = SingleOrDefaultMatch(candidates.Where(item =>
                MatchesExternalId(item) && MatchesNumber(item) && MatchesDate(item)));
            if (byExternalIdAndNumber is not null)
            {
                return byExternalIdAndNumber;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedNumber))
        {
            var byNumber = candidates
                .Where(item => MatchesNumber(item) && MatchesDate(item))
                .ToList();
            if (byNumber.Count == 1)
            {
                return byNumber[0];
            }

            if (byNumber.Count > 1)
            {
                throw new InvalidOperationException("Для акта найдено несколько счетов этого контрагента. Уточните связь; данные не изменены.");
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedExternalId))
        {
            var byExternalId = SingleOrDefaultMatch(candidates.Where(MatchesExternalId));
            if (byExternalId is not null)
            {
                return byExternalId;
            }
        }

        return null;
    }
}
