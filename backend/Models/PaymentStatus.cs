namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Статус платежа: ожидается, выполнен или просрочен.
    /// </summary>
    public enum PaymentStatus
    {
        /// <summary>
        /// Платёж ожидается.
        /// </summary>
        Pending,

        /// <summary>
        /// Платёж выполнен.
        /// </summary>
        Completed,

        /// <summary>
        /// Платёж просрочен.
        /// </summary>
        Overdue
    }
}
