namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Тип дохода (категория платежа по доходам).
    /// </summary>
    public class IncomeType
    {
        /// <summary>
        /// Цвет в HEX-формате.
        /// </summary>
        public string ColorHex { get; set; } = "#10B981";

        /// <summary>
        /// Дата создания.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Описание типа дохода.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Идентификатор типа дохода.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Признак активности.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Название типа дохода.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Платежи, относящиеся к данному типу дохода.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}