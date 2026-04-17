using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private static void RestoreLegalEntitySnapshot(LegalEntity entity, LegalEntitySnapshot snapshot)
    {
        entity.ShortName = snapshot.ShortName;
        entity.FullName = snapshot.FullName;
        entity.Inn = snapshot.Inn;
        entity.Phone = snapshot.Phone;
        entity.Email = snapshot.Email;
        entity.Director = snapshot.Director;
        entity.ResponsibleUserId = snapshot.ResponsibleUserId;
        entity.OrganizationForm = snapshot.OrganizationForm;
        entity.Notes = snapshot.Notes;
    }

    private async Task RestoreInvoiceSnapshotAsync(PaymentContext db, Payment payment, InvoiceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var originalInvoiceId = payment.InvoiceId;
        payment.Account = snapshot.Payment.Account;
        payment.AccountDate = snapshot.Payment.AccountDate;
        payment.Amount = snapshot.Payment.Amount;
        payment.Date = snapshot.Payment.Date;
        payment.PlannedDate = snapshot.Payment.PlannedDate;
        payment.Description = snapshot.Payment.Description;
        payment.Status = snapshot.Payment.Status;
        payment.Type = snapshot.Payment.Type;
        payment.IsPaid = snapshot.Payment.IsPaid;
        payment.PaidAmount = snapshot.Payment.PaidAmount;
        payment.PaidDate = snapshot.Payment.PaidDate;
        payment.LastPaymentDate = snapshot.Payment.LastPaymentDate;
        payment.Notes = snapshot.Payment.Notes;
        payment.SystemNotes = snapshot.Payment.SystemNotes;
        payment.ClientId = snapshot.Payment.ClientId;
        payment.ExpenseClientId = snapshot.Payment.ExpenseClientId;
        payment.ContractId = snapshot.Payment.ContractId;
        payment.DocumentKind = snapshot.Payment.DocumentKind;
        payment.PaymentSourceId = snapshot.Payment.PaymentSourceId;
        payment.ServiceId = snapshot.Payment.ServiceId;
        payment.IncomeTypeId = snapshot.Payment.IncomeTypeId;
        payment.DealTypeId = snapshot.Payment.DealTypeId;
        payment.PaymentStatusId = snapshot.Payment.PaymentStatusId;
        payment.IsNotForAccounting = snapshot.Payment.IsNotForAccounting;

        if (snapshot.InvoiceEntity is null)
        {
            payment.Invoice = null;
            payment.InvoiceId = null;
        }
        else
        {
            Invoice? invoice = payment.Invoice;
            if (invoice is null && snapshot.InvoiceEntity.Id > 0)
            {
                invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(item => item.Id == snapshot.InvoiceEntity.Id, cancellationToken);
            }

            invoice ??= new Invoice();
            invoice.Number = snapshot.InvoiceEntity.Number;
            invoice.Date = snapshot.InvoiceEntity.Date;
            if (invoice.Id == 0)
            {
                db.Invoices.Add(invoice);
            }

            payment.Invoice = invoice;
            payment.InvoiceId = invoice.Id > 0 ? invoice.Id : null;
        }

        SyncInvoiceLineItemsToSnapshot(db, payment, snapshot.LineItems);
        SyncResponsiblesToSnapshot(db, payment, snapshot.ResponsibleUserIds);

        await db.SaveChangesAsync(cancellationToken);

        if (originalInvoiceId.HasValue
            && originalInvoiceId != payment.InvoiceId
            && !await db.Payments.IgnoreQueryFilters().AnyAsync(item => item.InvoiceId == originalInvoiceId.Value && item.Id != payment.Id, cancellationToken))
        {
            var orphanInvoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(item => item.Id == originalInvoiceId.Value, cancellationToken);
            if (orphanInvoice is not null)
            {
                db.Remove(orphanInvoice);
            }
        }
    }

    private static void SyncInvoiceLineItemsToSnapshot(PaymentContext db, Payment payment, IReadOnlyCollection<InvoiceLineItemSnapshot> snapshots)
    {
        var existing = payment.InvoiceLineItems.OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToList();
        var desired = snapshots.OrderBy(item => item.SortOrder).ToList();

        for (var index = 0; index < desired.Count; index++)
        {
            var desiredItem = desired[index];
            var lineItem = index < existing.Count
                ? existing[index]
                : new InvoiceLineItem { Payment = payment };

            lineItem.SortOrder = desiredItem.SortOrder;
            lineItem.Name = desiredItem.Name;
            lineItem.Quantity = desiredItem.Quantity;
            lineItem.Unit = desiredItem.Unit;
            lineItem.Price = desiredItem.Price;

            if (lineItem.Id == 0 && !payment.InvoiceLineItems.Contains(lineItem))
            {
                payment.InvoiceLineItems.Add(lineItem);
            }
        }

        if (existing.Count > desired.Count)
        {
            db.RemoveRange(existing.Skip(desired.Count).ToList());
        }
    }

    private static void SyncResponsiblesToSnapshot(PaymentContext db, Payment payment, IReadOnlyCollection<int> responsibleUserIds)
    {
        var desiredIds = responsibleUserIds.Distinct().ToHashSet();
        var toRemove = payment.Responsibles.Where(item => !desiredIds.Contains(item.UserId)).ToList();
        if (toRemove.Count > 0)
        {
            db.RemoveRange(toRemove);
        }

        foreach (var userId in desiredIds)
        {
            if (payment.Responsibles.All(item => item.UserId != userId))
            {
                payment.Responsibles.Add(new PaymentResponsible
                {
                    Payment = payment,
                    UserId = userId,
                });
            }
        }
    }

    private static void RestoreActSnapshot(PaymentContext db, Act act, ActSnapshot snapshot)
    {
        act.Number = snapshot.Act.Number;
        act.Title = snapshot.Act.Title;
        act.Date = snapshot.Act.Date;
        act.Amount = snapshot.Act.Amount;
        act.CreationMode = snapshot.Act.CreationMode;
        act.InvoiceNumber = snapshot.Act.InvoiceNumber;
        act.InvoiceId = snapshot.Act.InvoiceId;
        act.CounterpartyInn = snapshot.Act.CounterpartyInn;
        act.Status = snapshot.Act.Status;
        act.ClientId = snapshot.Act.ClientId;
        act.ResponsibleId = snapshot.Act.ResponsibleId;
        act.Comment = snapshot.Act.Comment;

        var existing = act.LineItems.OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToList();
        var desired = snapshot.LineItems.OrderBy(item => item.SortOrder).ToList();
        for (var index = 0; index < desired.Count; index++)
        {
            var desiredItem = desired[index];
            var lineItem = index < existing.Count
                ? existing[index]
                : new ActLineItem { Act = act };

            lineItem.SortOrder = desiredItem.SortOrder;
            lineItem.SourceKey = desiredItem.SourceKey;
            lineItem.Name = desiredItem.Name;
            lineItem.Quantity = desiredItem.Quantity;
            lineItem.Unit = desiredItem.Unit;
            lineItem.Price = desiredItem.Price;

            if (lineItem.Id == 0 && !act.LineItems.Contains(lineItem))
            {
                act.LineItems.Add(lineItem);
            }
        }

        if (existing.Count > desired.Count)
        {
            db.RemoveRange(existing.Skip(desired.Count).ToList());
        }
    }

    private static async Task DeleteInvoiceAsync(PaymentContext db, Payment payment, CancellationToken cancellationToken)
    {
        var invoiceId = payment.InvoiceId;
        if (payment.InvoiceLineItems.Count > 0)
        {
            db.RemoveRange(payment.InvoiceLineItems);
        }

        if (payment.Responsibles.Count > 0)
        {
            db.RemoveRange(payment.Responsibles);
        }

        db.Remove(payment);
        await db.SaveChangesAsync(cancellationToken);

        if (invoiceId.HasValue
            && !await db.Payments.IgnoreQueryFilters().AnyAsync(item => item.InvoiceId == invoiceId.Value && item.Id != payment.Id, cancellationToken))
        {
            var invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(item => item.Id == invoiceId.Value, cancellationToken);
            if (invoice is not null)
            {
                db.Remove(invoice);
            }
        }
    }
}
