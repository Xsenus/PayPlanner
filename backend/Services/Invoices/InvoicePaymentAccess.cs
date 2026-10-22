using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Invoices;

/// <summary>Scopes an invoice as a complete aggregate, never as its visible fragment.</summary>
public static class InvoicePaymentAccess
{
    public static IQueryable<Payment> ScopeHeaders(PaymentContext db, SectionAccessContext access, int companyId)
        => companyId <= 0 ? db.Payments.AsNoTracking().Where(_ => false)
            : ScopeHeaders(db, access).Where(header => header.SellerCompanyId == companyId);

    public static IQueryable<Payment> ScopeHeaders(PaymentContext db, SectionAccessContext access)
    {
        var all = db.Payments.AsNoTracking();
        var visible = all.ApplyPaymentVisibility(access.Visibility).ApplyUnambiguousInvoiceLinks();
        if (access.Permission?.CanView != true) return all.Where(_ => false);
        return visible.Where(header => header.DocumentKind == DocumentKind.Invoice
            && header.InvoiceId.HasValue && header.Status != PaymentStatus.Cancelled && header.SellerCompanyId > 0
            && !all.Any(related => related.InvoiceId == header.InvoiceId && related.Status != PaymentStatus.Cancelled
                && (!related.SellerCompanyId.HasValue || related.SellerCompanyId != header.SellerCompanyId || related.Type != header.Type
                    || !visible.Any(allowed => allowed.Id == related.Id))));
    }
}
