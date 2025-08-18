namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Перечисление статусов дела клиента.
    /// </summary>
    public enum ClientCaseStatus
    {
        /// <summary>
        /// Дело открыто и находится в работе.
        /// </summary>
        Open = 0,

        /// <summary>
        /// Дело приостановлено.
        /// </summary>
        OnHold = 1,

        /// <summary>
        /// Дело закрыто.
        /// </summary>
        Closed = 2
    }
}