using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions
{
    public static class QueryExtensions
    {
        // ---------- Payments ----------
        public static IQueryable<Payment> WithPaymentIncludes(this IQueryable<Payment> q) =>
            q.Include(p => p.Client)
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