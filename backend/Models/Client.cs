namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Сущность клиента.
    /// </summary>
    public class Client
    {
        /// <summary>
        /// Почтовый адрес клиента.
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Коллекция дел (кейсов) клиента.
        /// </summary>
        public ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();

        /// <summary>
        /// Компания клиента (если применимо).
        /// </summary>
        public string Company { get; set; } = string.Empty;

        /// <summary>
        /// Дата и время создания записи (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Электронная почта клиента.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Уникальный идентификатор клиента.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Признак активного клиента.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Имя/название клиента.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Произвольные заметки по клиенту.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Коллекция платежей, связанных с клиентом.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        /// <summary>
        /// Телефон клиента.
        /// </summary>
        public string Phone { get; set; } = string.Empty;
    }
}