using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Сервис расчёта сводной статистики по платежам.
    /// Выполняет агрегирование на стороне БД и возвращает раздельные корзины
    /// для доходов и расходов (Completed/Pending/Overdue/Total/Remaining) за выбранный период.
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
        ///
        /// Приоритет периода:
        /// 1) Если заданы обе границы <paramref name="from"/> и <paramref name="to"/> — используются они (включительно).
        /// 2) Иначе применяется пресет <paramref name="period"/> (по умолчанию this-month внутри <c>PeriodHelper</c>).
        ///
        /// Агрегирование выполняется по комбинации Type+Status:
        /// для каждого типа (Доход/Расход) считаются суммы и количества по статусам (Completed, Pending, Overdue),
        /// а также Total и Remaining (Pending+Overdue).
        /// </summary>
        /// <param name="clientId">Фильтр по клиенту (опционально). Если не указан — учитываются все клиенты.</param>
        /// <param name="caseId">Фильтр по делу клиента (опционально). Если не указан — учитываются все дела.</param>
        /// <param name="from">Дата начала периода (включительно). При наличии обеих границ имеет приоритет над <paramref name="period"/>.</param>
        /// <param name="to">Дата окончания периода (включительно). При наличии обеих границ имеет приоритет над <paramref name="period"/>.</param>
        /// <param name="period">
        /// Ключ пресета периода (например: <c>today</c>, <c>last-7d</c>, <c>this-week</c>, <c>this-month</c>, <c>last-quarter</c>, <c>ytd</c>).
        /// Используется только если не заданы обе границы <paramref name="from"/> и <paramref name="to"/>.
        /// </param>
        /// <param name="type">Опциональный срез по типу платежей: только Доход (<c>Income</c>) или только Расход (<c>Expense</c>).</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>
        /// Объект <see cref="StatsSummary"/> с заполненными корзинами <see cref="StatsSummary.Income"/> и <see cref="StatsSummary.Expense"/>,
        /// а также нетто-показателями (<see cref="StatsSummary.NetCompleted"/>, <see cref="StatsSummary.NetTotal"/>, <see cref="StatsSummary.NetRemaining"/>).
        /// </returns>
        public async Task<StatsSummary> GetAsync(int? clientId, int? caseId, DateTime? from, DateTime? to,
            string? period, PaymentType? type, CancellationToken ct)
        {
            var (f, t) = PeriodHelper.Resolve(from, to, period);

            var q = _db.Payments.AsNoTracking().Where(x => x.Date >= f && x.Date <= t);

            if (clientId.HasValue) q = q.Where(x => x.ClientId == clientId.Value);
            if (caseId.HasValue) q = q.Where(x => x.ClientCaseId == caseId.Value);
            if (type.HasValue) q = q.Where(x => x.Type == type.Value);

            var grouped = await q.GroupBy(x => new { x.Type, x.Status })
              .Select(g => new
              {
                  g.Key.Type,
                  g.Key.Status,
                  Amount = g.Sum(v => (decimal?)v.Amount) ?? 0m,
                  Count = g.Count()
              })
              .ToListAsync(ct);

            StatsBucket MakeBucket(PaymentType tpe)
            {
                var byType = grouped.Where(x => x.Type == tpe);
                var completed = byType.Where(x => x.Status == PaymentStatus.Completed);
                var pending = byType.Where(x => x.Status == PaymentStatus.Pending);
                var overdue = byType.Where(x => x.Status == PaymentStatus.Overdue);

                var b = new StatsBucket
                {
                    CompletedAmount = completed.Sum(x => x.Amount),
                    CompletedCount = completed.Sum(x => x.Count),
                    PendingAmount = pending.Sum(x => x.Amount),
                    PendingCount = pending.Sum(x => x.Count),
                    OverdueAmount = overdue.Sum(x => x.Amount),
                    OverdueCount = overdue.Sum(x => x.Count),
                };

                b.TotalAmount = b.CompletedAmount + b.PendingAmount + b.OverdueAmount;
                b.TotalCount = b.CompletedCount + b.PendingCount + b.OverdueCount;
                return b;
            }

            var income = (!type.HasValue || type == PaymentType.Income)
                          ? MakeBucket(PaymentType.Income)
                          : new StatsBucket();

            var expense = (!type.HasValue || type == PaymentType.Expense)
                          ? MakeBucket(PaymentType.Expense)
                          : new StatsBucket();

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
