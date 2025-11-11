using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions
{
    public static class QueryExtensions
    {
        // ---------- Acts ----------
        public static IQueryable<Act> ApplyActFilters(
            this IQueryable<Act> q,
            DateTime? from,
            DateTime? to,
            ActStatus? status,
            int? clientId,
            int? responsibleId,
            string? search)
        {
            if (from.HasValue) q = q.Where(a => a.Date >= from.Value);
            if (to.HasValue) q = q.Where(a => a.Date <= to.Value);
            if (status.HasValue) q = q.Where(a => a.Status == status.Value);
            if (clientId.HasValue) q = q.Where(a => a.ClientId == clientId.Value);
            if (responsibleId.HasValue) q = q.Where(a => a.ResponsibleId == responsibleId.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                var like = $"%{term}%";
                q = q.Where(a =>
                    (a.Number != null && EF.Functions.Like(a.Number, like)) ||
                    (a.Title != null && EF.Functions.Like(a.Title, like)) ||
                    (a.InvoiceNumber != null && EF.Functions.Like(a.InvoiceNumber, like)) ||
                    (a.CounterpartyInn != null && EF.Functions.Like(a.CounterpartyInn, like)) ||
                    (a.Comment != null && EF.Functions.Like(a.Comment, like)) ||
                    (a.Client != null &&
                        ((a.Client.Name != null && EF.Functions.Like(a.Client.Name, like)) ||
                         (a.Client.Company != null && EF.Functions.Like(a.Client.Company, like)))) ||
                    (a.Responsible != null &&
                        (a.Responsible.FullName != null && EF.Functions.Like(a.Responsible.FullName, like)))
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
                 .ThenInclude(c => c.ClientStatus)
             .Include(p => p.ClientCase)
             .Include(p => p.DealType)
             .Include(p => p.IncomeType)
             .Include(p => p.PaymentSource)
             .Include(p => p.PaymentStatusEntity);

        public static IQueryable<Payment> ApplyPaymentFilters(
            this IQueryable<Payment> q,
            DateTime? from, DateTime? to, int? clientId, int? caseId, string? search)
        {
            if (from.HasValue) q = q.Where(p => p.Date >= from.Value);
            if (to.HasValue) q = q.Where(p => p.Date <= to.Value);
            if (clientId.HasValue) q = q.Where(p => p.ClientId == clientId.Value);
            if (caseId.HasValue) q = q.Where(p => p.ClientCaseId == caseId.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim();
                q = q.Where(p =>
                    (p.Description != null && EF.Functions.Like(p.Description, $"%{search}%")) ||
                    (p.Notes != null && EF.Functions.Like(p.Notes, $"%{search}%")) ||
                    (p.Account != null && EF.Functions.Like(p.Account, $"%{search}%")));
            }
            return q;
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
                search = search.Trim();
                q = q.Where(c =>
                    (c.Name != null && EF.Functions.Like(c.Name, $"%{search}%")) ||
                    (c.Email != null && EF.Functions.Like(c.Email, $"%{search}%")) ||
                    (c.Phone != null && EF.Functions.Like(c.Phone, $"%{search}%")) ||
                    (c.Company != null && EF.Functions.Like(c.Company, $"%{search}%")) ||
                    (c.Address != null && EF.Functions.Like(c.Address, $"%{search}%")));
            }
            if (isActive.HasValue) q = q.Where(c => c.IsActive == isActive.Value);
            return q;
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
                search = search.Trim();
                q = q.Where(c =>
                    (c.Title != null && EF.Functions.Like(c.Title, $"%{search}%")) ||
                    (c.Description != null && EF.Functions.Like(c.Description, $"%{search}%")));
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