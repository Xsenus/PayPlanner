namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Возможные статусы оплаты.
    /// </summary>
    public enum PaymentStatus
    {
        /// <summary>
        /// Ожидает оплаты.
        /// </summary>
        Pending,

        /// <summary>
        /// Оплачен полностью.
        /// </summary>
        Completed,

        /// <summary>
        /// Просрочен и требует внимания.
        /// </summary>
        Overdue,

        /// <summary>
        /// Платёж в процессе обработки.
        /// </summary>
        Processing,

        /// <summary>
        /// Платёж отменён.
        /// </summary>
        Cancelled
    }
}
