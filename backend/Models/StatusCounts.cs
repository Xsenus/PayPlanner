namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Количественная статистика по статусам платежей.
    /// </summary>
    public class StatusCounts
    {
        /// <summary>
        /// Количество завершённых платежей.
        /// </summary>
        public int Completed { get; set; }

        /// <summary>
        /// Количество просроченных платежей.
        /// </summary>
        public int Overdue { get; set; }

        /// <summary>
        /// Количество ожидающих платежей.
        /// </summary>
        public int Pending { get; set; }

        /// <summary>
        /// Общее количество платежей.
        /// </summary>
        public int Total { get; set; }
    }
}