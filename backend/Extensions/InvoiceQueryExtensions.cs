using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions;

public static class InvoiceQueryExtensions
{
    public const string ConflictMessage = "Связи счёта противоречат друг другу. Требуется проверка администратором; данные не изменены.";

    public static Task<bool> HasAmbiguousInvoiceLinksAsync(this IQueryable<Payment> payments, CancellationToken ct = default)
        => payments.Select(payment => payment.Id)
            .Except(payments.ApplyUnambiguousInvoiceLinks().Select(payment => payment.Id)).AnyAsync(ct);

    // Contradictory historical links require a separate reviewed data repair. Never combine them on read/write.
    public static IQueryable<Payment> ApplyUnambiguousInvoiceLinks(this IQueryable<Payment> payments)
        => payments.Where(payment => !payment.InvoiceId.HasValue || (payment.Invoice != null
            && !payment.Invoice.Payments.Any(document => document.DocumentKind == DocumentKind.Invoice
                && payment.Invoice.Payments.Any(other =>
                    (other.ClientId.HasValue && other.ClientId != document.ClientId)
                    || (other.LegalEntityId.HasValue && other.LegalEntityId != document.LegalEntityId)
                    || (other.SellerCompanyId.HasValue && other.SellerCompanyId != document.SellerCompanyId)
                    || (other.SellerBranchId.HasValue && other.SellerBranchId != document.SellerBranchId)
                    || (other.DocumentKind == DocumentKind.Invoice
                        && (other.ClientId != document.ClientId || other.LegalEntityId != document.LegalEntityId
                            || other.Type != document.Type || other.SellerCompanyId != document.SellerCompanyId
                            || other.SellerBranchId != document.SellerBranchId))))));

    // Keep the established editor identity independent of registry display aggregation and search.
    public static IQueryable<Payment> ApplyInvoiceRepresentativeFilter(
        this IQueryable<Payment> visiblePayments, IQueryable<Payment> allPayments)
        => visiblePayments.Where(payment =>
            (payment.InvoiceId.HasValue && payment.Invoice != null
                && payment.Id == allPayments
                    .Where(candidate => candidate.InvoiceId == payment.InvoiceId)
                    .OrderByDescending(candidate => candidate.DocumentKind == DocumentKind.Invoice)
                    .ThenBy(candidate => candidate.Id)
                    .Select(candidate => candidate.Id)
                    .FirstOrDefault())
            || (!payment.InvoiceId.HasValue && payment.DocumentKind == DocumentKind.Invoice
                && payment.Account != null && payment.Account != ""));
}
