namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Сводная статистика по платежам за период с фильтрами по клиенту/делу/типу.
    /// Содержит отдельные корзины для Доходов и Расходов, а также нетто-показатели.
    /// </summary>
    public class StatsSummary
    {
        /// <summary>
        /// Идентификатор дела (если применён фильтр).
        /// </summary>
        public int? CaseId { get; set; }

        /// <summary>
        /// Идентификатор клиента (если применён фильтр).
        /// </summary>
        public int? ClientId { get; set; }

        /// <summary>
        /// Корзина показателей для платежей типа Расход.
        /// </summary>
        public StatsBucket Expense { get; set; } = new();

        /// <summary>
        /// Дата начала периода (включительно).
        /// </summary>
        public DateTime From { get; set; }

        /// <summary>
        /// Корзина показателей для платежей типа Доход.
        /// </summary>
        public StatsBucket Income { get; set; } = new();

        /// <summary>
        /// Нетто по выполненным платежам: Income.CompletedAmount - Expense.CompletedAmount.
        /// </summary>
        public decimal NetCompleted => Income.CompletedAmount - Expense.CompletedAmount;

        /// <summary>
        /// Нетто по остаткам: Income.RemainingAmount - Expense.RemainingAmount.
        /// </summary>
        public decimal NetRemaining => Income.RemainingAmount - Expense.RemainingAmount;

        /// <summary>
        /// Нетто по общей сумме: Income.TotalAmount - Expense.TotalAmount.
        /// </summary>
        public decimal NetTotal => Income.TotalAmount - Expense.TotalAmount;

        /// <summary>
        /// Дата окончания периода (включительно).
        /// </summary>
        public DateTime To { get; set; }
    }
}