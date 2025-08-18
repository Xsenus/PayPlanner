namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Статистика по клиенту, включая суммы, количество платежей и даты.
    /// </summary>
    public class ClientStats
    {
        /// <summary>
        /// Идентификатор клиента.
        /// </summary>
        public int ClientId { get; set; }

        /// <summary>
        /// Имя клиента.
        /// </summary>
        public string ClientName { get; set; } = string.Empty;

        /// <summary>
        /// Дата последнего платежа.
        /// </summary>
        public DateTime? LastPaymentDate { get; set; }

        /// <summary>
        /// Чистая сумма (доходы минус расходы).
        /// </summary>
        public decimal NetAmount { get; set; }

        /// <summary>
        /// Количество оплаченных платежей.
        /// </summary>
        public int PaidPayments { get; set; }

        /// <summary>
        /// Количество ожидающих платежей.
        /// </summary>
        public int PendingPayments { get; set; }

        /// <summary>
        /// Список последних платежей.
        /// </summary>
        public List<Payment> RecentPayments { get; set; } = new();

        /// <summary>
        /// Общая сумма расходов клиента.
        /// </summary>
        public decimal TotalExpenses { get; set; }

        /// <summary>
        /// Общая сумма доходов клиента.
        /// </summary>
        public decimal TotalIncome { get; set; }

        /// <summary>
        /// Общее количество платежей.
        /// </summary>
        public int TotalPayments { get; set; }
    }
}