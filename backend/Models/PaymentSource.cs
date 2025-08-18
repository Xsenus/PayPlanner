namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Источник платежа (например, банк, касса).
    /// </summary>
    public class PaymentSource
    {
        /// <summary>
        /// Цвет в HEX-формате.
        /// </summary>
        public string ColorHex { get; set; } = "#6B7280";

        /// <summary>
        /// Дата создания.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Описание источника.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Идентификатор источника.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Признак активности.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Название источника.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Платежи, относящиеся к данному источнику.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}