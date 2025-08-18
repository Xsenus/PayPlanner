using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Дело/задача клиента (кейc), к которому можно привязывать платежи.
    /// </summary>
    public class ClientCase
    {
        /// <summary>
        /// Навигация на клиента-владельца.
        /// </summary>
        public Client? Client { get; set; }

        /// <summary>
        /// Идентификатор клиента-владельца дела.
        /// </summary>
        public int ClientId { get; set; }

        /// <summary>
        /// Дата создания дела (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Описание или примечания по делу.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Уникальный идентификатор дела.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Платежи, привязанные к этому делу.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        /// <summary>
        /// Статус дела (черновик/в работе/закрыто).
        /// </summary>
        public ClientCaseStatus Status { get; set; } = ClientCaseStatus.Open;

        /// <summary>
        /// Короткое название/тема дела.
        /// </summary>
        [MaxLength(200)]
        public string Title { get; set; } = string.Empty;
    }
}