namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Месячная статистика по доходам, расходам и статусам платежей.
    /// </summary>
    public class MonthlyStats
    {
        /// <summary>
        /// Процент выполнения (от 0 до 100).
        /// </summary>
        public double CompletionRate { get; set; }

        /// <summary>
        /// Статистика по статусам платежей.
        /// </summary>
        public StatusCounts Counts { get; set; } = new();

        /// <summary>
        /// Общая сумма расходов.
        /// </summary>
        public decimal Expense { get; set; }

        /// <summary>
        /// Общая сумма доходов.
        /// </summary>
        public decimal Income { get; set; }

        /// <summary>
        /// Прибыль (доходы минус расходы).
        /// </summary>
        public decimal Profit { get; set; }
    }
}