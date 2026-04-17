using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions
{
    public static class QueryExtensions
    {
        private const string InstallmentPaymentMarker = "[pp:installment-payment]";

        // ---------- Acts ----------
        public static IQueryable<Act> ApplyActFilters(
            this IQueryable<Act> q,
            DateTime? from,
            DateTime? to,
            ActStatus? status,
            PaymentType? type,
            int? clientId,
            int? responsibleId,
            string? search)
        {
            if (from.HasValue) q = q.Where(a => a.Date >= from.Value);
            if (to.HasValue) q = q.Where(a => a.Date <= to.Value);
            if (status.HasValue) q = q.Where(a => a.Status == status.Value);
            if (type.HasValue) q = q.Where(a => a.Invoice != null && a.Invoice.Type == type.Value);
            if (clientId.HasValue) q = q.Where(a => a.ClientId == clientId.Value);
            if (responsibleId.HasValue) q = q.Where(a => a.ResponsibleId == responsibleId.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                q = q.Where(a =>
                    (a.Number != null && a.Number.ToLower().Contains(term)) ||
                    (a.Title != null && a.Title.ToLower().Contains(term)) ||
                    (a.InvoiceNumber != null && a.InvoiceNumber.ToLower().Contains(term)) ||
                    (a.CounterpartyInn != null && a.CounterpartyInn.ToLower().Contains(term)) ||
                    (a.Comment != null && a.Comment.ToLower().Contains(term)) ||
                    (a.Client != null &&
                        ((a.Client.Name != null && a.Client.Name.ToLower().Contains(term)) ||
                         (a.Client.Company != null && a.Client.Company.ToLower().Contains(term)))) ||
                    (a.Responsible != null &&
                        (a.Responsible.FullName != null && a.Responsible.FullName.ToLower().Contains(term)))
                );
            }

            return q;
        }

        public static IQueryable<Act> ApplyActSort(this IQueryable<Act> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "number" => desc ? q.OrderByDescending(a => a.Number) : q.OrderBy(a => a.Number),
                "amount" => desc ? q.OrderByDescending(a => a.Amount) : q.OrderBy(a => a.Amount),
                "invoicenumber" => desc
                    ? q.OrderByDescending(a => a.InvoiceNumber ?? string.Empty)
                    : q.OrderBy(a => a.InvoiceNumber ?? string.Empty),
                "status" => desc ? q.OrderByDescending(a => a.Status) : q.OrderBy(a => a.Status),
                "client" => desc
                    ? q.OrderByDescending(a => a.Client != null ? a.Client.Name : string.Empty)
                    : q.OrderBy(a => a.Client != null ? a.Client.Name : string.Empty),
                "inn" or "counterpartyinn" => desc
                    ? q.OrderByDescending(a => a.CounterpartyInn ?? string.Empty)
                    : q.OrderBy(a => a.CounterpartyInn ?? string.Empty),
                "responsible" => desc
                    ? q.OrderByDescending(a => a.Responsible != null ? a.Responsible.FullName : string.Empty)
                    : q.OrderBy(a => a.Responsible != null ? a.Responsible.FullName : string.Empty),
                "createdat" => desc ? q.OrderByDescending(a => a.CreatedAt) : q.OrderBy(a => a.CreatedAt),
                _ => desc ? q.OrderByDescending(a => a.Date) : q.OrderBy(a => a.Date),
            };
        }

        // ---------- Payments ----------
        public static IQueryable<Payment> WithPaymentIncludes(this IQueryable<Payment> q) =>
            q.Include(p => p.Client)
                 .ThenInclude(c => c!.ClientStatus)
              .Include(p => p.ClientCase)
              .Include(p => p.DealType)
              .Include(p => p.IncomeType)
             .Include(p => p.PaymentSource)
             .Include(p => p.Service)
             .Include(p => p.PaymentStatusEntity);

        public static IQueryable<Payment> ApplyPaymentFilters(
            this IQueryable<Payment> q,
            DateTime? from, DateTime? to, int? clientId, int? caseId, string? search)
        {
            if (from.HasValue)
            {
                var fromDate = from.Value.Date;
                q = q.Where(p => p.Date >= fromDate);
            }

            if (to.HasValue)
            {
                var toDate = to.Value.Date;
                q = q.Where(p => p.Date <= toDate);
            }

            if (clientId.HasValue)
            {
                q = q.Where(p =>
                    p.ClientId == clientId.Value ||
                    p.ExpenseClientId == clientId.Value);
            }

            if (caseId.HasValue)
            {
                q = q.Where(p =>
                    p.ClientCaseId == caseId.Value ||
                    p.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                q = q.Where(p =>
                    (p.Description != null && p.Description.ToLower().Contains(search)) ||
                    (p.Notes != null && p.Notes.ToLower().Contains(search)) ||
                    (p.Account != null && p.Account.ToLower().Contains(search)) ||
                    (p.Client != null &&
                        ((p.Client.Name != null && p.Client.Name.ToLower().Contains(search)) ||
                         (p.Client.Company != null && p.Client.Company.ToLower().Contains(search)))) ||
                    (p.ClientCase != null && p.ClientCase.Title != null && p.ClientCase.Title.ToLower().Contains(search)) ||
                    (p.DealType != null && p.DealType.Name != null && p.DealType.Name.ToLower().Contains(search)) ||
                    (p.IncomeType != null && p.IncomeType.Name != null && p.IncomeType.Name.ToLower().Contains(search)) ||
                    (p.PaymentSource != null && p.PaymentSource.Name != null && p.PaymentSource.Name.ToLower().Contains(search)));
            }
            return q;
        }

        public static IQueryable<Payment> ExcludeInstallmentParentInvoices(
            this IQueryable<Payment> q,
            PaymentContext db,
            bool excludeInstallmentParents = true)
        {
            if (!excludeInstallmentParents)
            {
                return q;
            }

            return q.Where(payment =>
                payment.DocumentKind != DocumentKind.Invoice
                || payment.InvoiceId == null
                || !db.Payments.Any(child =>
                    child.InvoiceId == payment.InvoiceId
                    && child.Id != payment.Id
                    && child.DocumentKind != DocumentKind.Invoice
                    && child.SystemNotes != null
                    && child.SystemNotes.Contains(InstallmentPaymentMarker)));
        }

        public static IQueryable<Payment> ApplyPaymentSort(this IQueryable<Payment> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "amount" => desc ? q.OrderByDescending(p => p.Amount) : q.OrderBy(p => p.Amount),
                "createdat" => desc ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
                _ => desc ? q.OrderByDescending(p => p.Date) : q.OrderBy(p => p.Date), // date по умолчанию
            };
        }

        // ---------- Clients ----------
        public static IQueryable<Client> ApplyClientFilters(this IQueryable<Client> q, string? search, bool? isActive)
        {
            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                q = q.Where(c =>
                    (c.Name != null && c.Name.ToLower().Contains(search)) ||
                    (c.Email != null && c.Email.ToLower().Contains(search)) ||
                    (c.Phone != null && c.Phone.ToLower().Contains(search)) ||
                    (c.Company != null && c.Company.ToLower().Contains(search)) ||
                    (c.Address != null && c.Address.ToLower().Contains(search)));
            }
            if (isActive.HasValue) q = q.Where(c => c.IsActive == isActive.Value);
            return q;
        }

        public static IQueryable<Client> ApplyClientVisibility(this IQueryable<Client> q, bool isAdmin, int? responsibleUserId)
        {
            if (isAdmin) return q;
            if (!responsibleUserId.HasValue) return q.Where(_ => false);
            return q.Where(c => c.ResponsibleUserId == responsibleUserId.Value);
        }

        public static IQueryable<Client> ApplyClientSort(this IQueryable<Client> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "createdat" => desc ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt),
                _ => desc ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name), // name по умолчанию
            };
        }

        // ---------- Cases ----------
        public static IQueryable<ClientCase> WithCaseIncludes(this IQueryable<ClientCase> q, bool includePayments = false)
        {
            q = q.Include(c => c.Client);
            if (includePayments) q = q.Include(c => c.Payments);
            return q;
        }

        public static IQueryable<ClientCase> ApplyCaseFilters(
            this IQueryable<ClientCase> q,
            int? clientId, ClientCaseStatus? status, string? search)
        {
            if (clientId.HasValue) q = q.Where(c => c.ClientId == clientId.Value);
            if (status.HasValue) q = q.Where(c => c.Status == status.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                q = q.Where(c =>
                    (c.Title != null && c.Title.ToLower().Contains(search)) ||
                    (c.Description != null && c.Description.ToLower().Contains(search)));
            }
            return q;
        }

        public static IQueryable<ClientCase> ApplyCaseSort(this IQueryable<ClientCase> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "title" => desc ? q.OrderByDescending(c => c.Title) : q.OrderBy(c => c.Title),
                "status" => desc ? q.OrderByDescending(c => c.Status) : q.OrderBy(c => c.Status),
                _ => desc ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt),
            };
        }
    }
}
