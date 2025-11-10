namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Aggregated metrics for a month in the legacy stats endpoint.
    /// </summary>
    public class MonthlyStats
    {
        /// <summary>
        /// Share of completed payments (0 to 100).
        /// </summary>
        public double CompletionRate { get; set; }

        /// <summary>
        /// Number of payments per status.
        /// </summary>
        public StatusCounts Counts { get; set; } = new();

        /// <summary>
        /// Total paid expenses.
        /// </summary>
        public decimal Expense { get; set; }

        /// <summary>
        /// Total paid income.
        /// </summary>
        public decimal Income { get; set; }

        /// <summary>
        /// Net balance (income - expense).
        /// </summary>
        public decimal Profit { get; set; }

        /// <summary>
        /// Amount collected for completed payments.
        /// </summary>
        public decimal CompletedAmount { get; set; }

        /// <summary>
        /// Outstanding amount for pending payments.
        /// </summary>
        public decimal PendingAmount { get; set; }

        /// <summary>
        /// Outstanding amount for overdue payments.
        /// </summary>
        public decimal OverdueAmount { get; set; }
    }
}
