namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Набор агрегированных показателей для одного типа платежей (Доходы или Расходы)
    /// за выбранный период и с учётом применённых фильтров.
    /// </summary>
    public class StatsBucket
    {
        /// <summary>
        /// Сумма фактически полученных/оплаченных средств (вне зависимости от статуса).
        /// </summary>
        public decimal CollectedAmount { get; set; }

        /// <summary>
        /// Сумма платежей со статусом Completed (выполнено/оплачено).
        /// </summary>
        public decimal CompletedAmount { get; set; }

        /// <summary>
        /// Количество платежей со статусом Completed.
        /// </summary>
        public int CompletedCount { get; set; }

        /// <summary>
        /// Сумма платежей со статусом Overdue (просрочено).
        /// </summary>
        public decimal OverdueAmount { get; set; }

        /// <summary>
        /// Количество платежей со статусом Overdue.
        /// </summary>
        public int OverdueCount { get; set; }

        /// <summary>
        /// Сумма платежей со статусом Pending (ожидается).
        /// </summary>
        public decimal PendingAmount { get; set; }

        /// <summary>
        /// Количество платежей со статусом Pending.
        /// </summary>
        public int PendingCount { get; set; }

        /// <summary>
        /// Остаток к получению/оплате: PendingAmount + OverdueAmount.
        /// </summary>
        public decimal RemainingAmount => PendingAmount + OverdueAmount;

        /// <summary>
        /// Общая сумма по всем статусам (Completed + Pending + Overdue).
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// Общее количество платежей по всем статусам.
        /// </summary>
        public int TotalCount { get; set; }
    }
}