namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Тип платежа: приход или расход.
    /// </summary>
    public enum PaymentType
    {
        /// <summary>
        /// Приходные операции (доход).
        /// </summary>
        Income,

        /// <summary>
        /// Расходные операции.
        /// </summary>
        Expense
    }
}