using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Сервис расчёта сводной статистики по платежам.
    /// Выполняет агрегирование по платежам за выбранный период, предварительно
    /// убирая представительские счета, которые дублируют график рассрочки.
    /// </summary>
    public class StatsSummaryService
    {
        private readonly PaymentContext _db;

        /// <summary>
        /// Создаёт экземпляр сервиса сводной статистики.
        /// </summary>
        /// <param name="db">EF Core контекст с таблицей платежей.</param>
        public StatsSummaryService(PaymentContext db) => _db = db;

        /// <summary>
        /// Рассчитать сводную статистику по платежам с фильтрами.
        /// </summary>
        public async Task<StatsSummary> GetAsync(int? clientId, int? caseId, DateTime? from, DateTime? to,
            string? period, PaymentType? type, PaymentStatus? status, string? q, string? clientRole, CancellationToken ct)
        {
            var (f, t) = PeriodHelper.Resolve(from, to, period);

            var qry = _db.Payments
                .AsNoTracking()
                .ExcludeInstallmentParentInvoices(_db)
                .Where(x => x.Date >= f && x.Date <= t);

            if (clientId.HasValue)
            {
                var selectedClientId = clientId.Value;
                var normalizedClientRole = QueryExtensions.NormalizeClientRole(clientRole);
                qry = normalizedClientRole switch
                {
                    "counterparty" => qry.Where(x => x.ClientId == selectedClientId),
                    "expenseClient" => qry.Where(x => x.ExpenseClientId == selectedClientId),
                    _ => qry.Where(x =>
                        (x.Type == PaymentType.Income && x.ClientId == selectedClientId) ||
                        (x.Type == PaymentType.Expense &&
                            (x.ExpenseClientId == selectedClientId ||
                             (x.ExpenseClientId == null && x.ClientId == selectedClientId)))),
                };
            }

            if (caseId.HasValue)
            {
                qry = qry.Where(x =>
                    x.ClientCaseId == caseId.Value ||
                    x.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value));
            }

            if (type.HasValue)
            {
                qry = qry.Where(x => x.Type == type.Value);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = q.Trim().ToLower();

                qry = qry.Where(p =>
                    (p.Description ?? "").ToLower().Contains(s) ||
                    (p.Notes ?? "").ToLower().Contains(s) ||
                    (p.Client != null && (p.Client.Name ?? "").ToLower().Contains(s)) ||
                    (p.DealType != null && (p.DealType.Name ?? "").ToLower().Contains(s)) ||
                    (p.IncomeType != null && (p.IncomeType.Name ?? "").ToLower().Contains(s)) ||
                    (p.PaymentSource != null && (p.PaymentSource.Name ?? "").ToLower().Contains(s))
                );
            }

            var payments = await qry
                .Select(x => new Payment
                {
                    Id = x.Id,
                    InvoiceId = x.InvoiceId,
                    Account = x.Account,
                    AccountDate = x.AccountDate,
                    LegacyAccountNumber = x.LegacyAccountNumber,
                    LegacyAccountDate = x.LegacyAccountDate,
                    Amount = x.Amount,
                    PaidAmount = x.PaidAmount,
                    Date = x.Date,
                    ClientId = x.ClientId,
                    ExpenseClientId = x.ExpenseClientId,
                    ClientCaseId = x.ClientCaseId,
                    Type = x.Type,
                    Status = x.Status,
                    DocumentKind = x.DocumentKind,
                    SystemNotes = x.SystemNotes,
                })
                .ToListAsync(ct);

            var visiblePayments = InvoiceManagedPaymentRules
                .FilterVisiblePayments(payments)
                .Where(payment => !status.HasValue || payment.Status == status.Value)
                .ToList();

            StatsBucket MakeBucket(PaymentType paymentType)
            {
                var byType = visiblePayments
                    .Where(payment => payment.Type == paymentType && payment.Status != PaymentStatus.Cancelled)
                    .ToList();

                var completed = byType.Where(payment => payment.Status == PaymentStatus.Completed).ToList();
                var pending = byType.Where(payment => payment.Status == PaymentStatus.Pending || payment.Status == PaymentStatus.Processing).ToList();
                var overdue = byType.Where(payment => payment.Status == PaymentStatus.Overdue).ToList();

                return new StatsBucket
                {
                    CompletedAmount = completed.Sum(payment => payment.PaidAmount),
                    CompletedCount = completed.Count,
                    PendingAmount = pending.Sum(payment => payment.OutstandingAmount),
                    PendingCount = pending.Count,
                    OverdueAmount = overdue.Sum(payment => payment.OutstandingAmount),
                    OverdueCount = overdue.Count,
                    CollectedAmount = byType.Sum(payment => payment.PaidAmount),
                    TotalAmount = byType.Sum(payment => payment.Amount),
                    TotalCount = byType.Count,
                };
            }

            var income = (!type.HasValue || type == PaymentType.Income) ? MakeBucket(PaymentType.Income) : new StatsBucket();
            var expense = (!type.HasValue || type == PaymentType.Expense) ? MakeBucket(PaymentType.Expense) : new StatsBucket();

            return new StatsSummary
            {
                From = f,
                To = t,
                ClientId = clientId,
                CaseId = caseId,
                Income = income,
                Expense = expense
            };
        }
    }
}
