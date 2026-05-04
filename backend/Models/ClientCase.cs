using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
        public int? ClientId { get; set; }

        /// <summary>
        /// Навигация на юридическое лицо-владельца.
        /// </summary>
        public LegalEntity? LegalEntity { get; set; }

        /// <summary>
        /// Идентификатор юридического лица-владельца дела.
        /// </summary>
        public int? LegalEntityId { get; set; }

        /// <summary>
        /// Дата создания дела (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [NotMapped]
        public int? AuditCreatedByUserId { get; set; }

        [NotMapped]
        public string? AuditCreatedByUserName { get; set; }

        [NotMapped]
        public DateTime? AuditUpdatedAt { get; set; }

        [NotMapped]
        public int? AuditUpdatedByUserId { get; set; }

        [NotMapped]
        public string? AuditUpdatedByUserName { get; set; }

        [NotMapped]
        public bool AuditIsDeleted { get; set; }

        [NotMapped]
        public DateTime? AuditDeletedAt { get; set; }

        [NotMapped]
        public int? AuditDeletedByUserId { get; set; }

        [NotMapped]
        public string? AuditDeletedByUserName { get; set; }

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
        /// Счета, связанные с делом через отдельную таблицу связей.
        /// </summary>
        public ICollection<InvoiceClientCase> InvoiceClientCases { get; set; } = new List<InvoiceClientCase>();

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
