using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions;

public static class InvoiceActQueryExtensions
{
    public static IQueryable<Act> ApplyInvoiceActOwnership(this IQueryable<Act> acts, IQueryable<Payment> payments)
        => acts.Where(act => act.InvoiceId.HasValue
            ? act.Invoice != null
                && (!act.ClientId.HasValue || act.ClientId == act.Invoice.ClientId)
                && (!act.LegalEntityId.HasValue || act.LegalEntityId == act.Invoice.LegalEntityId)
                && (!act.SellerCompanyId.HasValue || act.SellerCompanyId == act.Invoice.SellerCompanyId)
                && (!act.SellerBranchId.HasValue || act.SellerBranchId == act.Invoice.SellerBranchId)
            : (act.ClientId.HasValue || act.LegalEntityId.HasValue)
                && payments.Count(document => document.DocumentKind == DocumentKind.Invoice
                    && document.ClientId == act.ClientId && document.LegalEntityId == act.LegalEntityId
                    && (!act.SellerCompanyId.HasValue || act.SellerCompanyId == document.SellerCompanyId)
                    && (!act.SellerBranchId.HasValue || act.SellerBranchId == document.SellerBranchId)
                    && (document.Invoice != null ? document.Invoice.Number : document.Account) == act.InvoiceNumber) == 1);
}
