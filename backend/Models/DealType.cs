namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Тип сделки (категория платежа).
    /// </summary>
    public class DealType
    {
        /// <summary>
        /// Цвет в HEX-формате.
        /// </summary>
        public string ColorHex { get; set; } = "#3B82F6";

        /// <summary>
        /// Дата создания.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Описание типа сделки.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Идентификатор типа сделки.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Признак активности.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Название типа сделки.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Платежи, относящиеся к данному типу сделки.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}