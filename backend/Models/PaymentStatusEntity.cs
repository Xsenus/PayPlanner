namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Справочник статусов платежа.
    /// </summary>
    public class PaymentStatusEntity
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
        /// Описание статуса.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Идентификатор статуса.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Признак активности.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Название статуса.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Платежи, относящиеся к данному статусу.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}