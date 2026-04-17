using Microsoft.EntityFrameworkCore;
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
        act.Date = (draft.Date ?? draft.PaymentDate ?? DateTime.UtcNow).Date;
        act.Amount = Math.Max(0m, draft.Amount ?? 0m);
        act.CreationMode = ActCreationMode.Manual;
        act.InvoiceNumber = Truncate(draft.InvoiceNumber ?? invoicePayment?.Account ?? invoicePayment?.Invoice?.Number, 100);
        act.InvoiceId = invoicePayment?.Id;
        act.CounterpartyInn = Truncate(draft.CounterpartyInn, 32);
        act.Status = MapActStatus(draft.StatusText);
        act.ClientId = clientId;
        act.ResponsibleId = responsibleId ?? act.ResponsibleId;
        if (string.IsNullOrWhiteSpace(act.Comment) && draft.ParseWarnings.Count > 0)
        {
            act.Comment = Truncate(string.Join("; ", draft.ParseWarnings), 1000);
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

    private async Task<Payment?> FindInvoicePaymentByNumberAsync(PaymentContext db, string? invoiceNumber, CancellationToken cancellationToken)
    {
        var normalizedNumber = NormalizeDocumentNumber(invoiceNumber);
        if (string.IsNullOrWhiteSpace(normalizedNumber))
        {
            return null;
        }

        var candidates = await db.Payments
            .AsNoTracking()
            .Include(item => item.Invoice)
            .Include(item => item.InvoiceLineItems)
            .Where(item => item.DocumentKind == DocumentKind.Invoice)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(item =>
            string.Equals(NormalizeDocumentNumber(item.Invoice?.Number), normalizedNumber, StringComparison.Ordinal)
            || string.Equals(NormalizeDocumentNumber(item.Account), normalizedNumber, StringComparison.Ordinal)
            || string.Equals(NormalizeDocumentNumber(item.LegacyAccountNumber), normalizedNumber, StringComparison.Ordinal));
    }
}
