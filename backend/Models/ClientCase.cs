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

        /// <summary>
        /// Внутренний номер дела в CRM.
        /// </summary>
        [MaxLength(64)]
        public string CaseNumber { get; set; } = string.Empty;

        /// <summary>
        /// Тип/категория дела: наследство, банкротство, семейное право и т.д.
        /// </summary>
        [MaxLength(120)]
        public string CaseType { get; set; } = string.Empty;

        /// <summary>
        /// Текущий этап работы по делу.
        /// </summary>
        [MaxLength(160)]
        public string Stage { get; set; } = string.Empty;

        /// <summary>
        /// Ответственный юрист по делу. Если не задан, используется ответственный владельца.
        /// </summary>
        public int? ResponsibleUserId { get; set; }

        public User? ResponsibleUser { get; set; }

        /// <summary>
        /// Дата начала ведения дела.
        /// </summary>
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// Плановый крайний срок/дата завершения дела.
        /// </summary>
        public DateTime? DueDate { get; set; }

        /// <summary>
        /// Сумма гонорара/договора по делу.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal FeeAmount { get; set; }

        /// <summary>
        /// Приоритет дела: low, normal, high, urgent.
        /// </summary>
        [MaxLength(32)]
        public string Priority { get; set; } = "normal";

        /// <summary>
        /// Наименование суда, если дело судебное.
        /// </summary>
        [MaxLength(200)]
        public string CourtName { get; set; } = string.Empty;

        /// <summary>
        /// Номер дела, присвоенный судом.
        /// </summary>
        [MaxLength(100)]
        public string CourtCaseNumber { get; set; } = string.Empty;

        /// <summary>
        /// Ссылка на карточку дела на сайте суда.
        /// </summary>
        [MaxLength(1000)]
        public string CourtUrl { get; set; } = string.Empty;

        /// <summary>
        /// Дата ближайшего судебного заседания.
        /// </summary>
        public DateTime? NextHearingAt { get; set; }

        /// <summary>
        /// Истец/заявитель по делу.
        /// </summary>
        [MaxLength(300)]
        public string Claimant { get; set; } = string.Empty;

        /// <summary>
        /// Ответчик/должник по делу.
        /// </summary>
        [MaxLength(300)]
        public string Defendant { get; set; } = string.Empty;

        /// <summary>
        /// Третьи лица и иные участники процесса.
        /// </summary>
        [MaxLength(1000)]
        public string ThirdParties { get; set; } = string.Empty;

        /// <summary>
        /// Представители и контакты участников по делу.
        /// </summary>
        [MaxLength(1000)]
        public string Representatives { get; set; } = string.Empty;

        /// <summary>
        /// Судья по делу.
        /// </summary>
        [MaxLength(200)]
        public string JudgeName { get; set; } = string.Empty;

        /// <summary>
        /// Номер зала/кабинета или информация о месте заседания.
        /// </summary>
        [MaxLength(120)]
        public string Courtroom { get; set; } = string.Empty;

        /// <summary>
        /// Контакты секретаря/канцелярии суда.
        /// </summary>
        [MaxLength(500)]
        public string CourtSecretaryContacts { get; set; } = string.Empty;

        /// <summary>
        /// Дата подачи иска/заявления.
        /// </summary>
        public DateTime? ClaimFiledAt { get; set; }

        public DateTime? CourtDecisionAt { get; set; }

        /// <summary>
        /// Срок обжалования решения.
        /// </summary>
        public DateTime? AppealDeadlineAt { get; set; }

        public DateTime? CassationDeadlineAt { get; set; }

        /// <summary>
        /// Ближайший срок подачи ходатайств/документов.
        /// </summary>
        public DateTime? MotionDeadlineAt { get; set; }

        /// <summary>
        /// Ближайший срок подачи возражений/отзывов.
        /// </summary>
        public DateTime? ObjectionDeadlineAt { get; set; }

        /// <summary>
        /// Реестр документов по делу: тип, статус, дата и где лежит файл.
        /// </summary>
        [MaxLength(4000)]
        public string DocumentsSummary { get; set; } = string.Empty;

        /// <summary>
        /// Хронология событий по делу.
        /// </summary>
        [MaxLength(4000)]
        public string EventHistory { get; set; } = string.Empty;

        /// <summary>
        /// План действий/стратегия юриста.
        /// </summary>
        [MaxLength(4000)]
        public string Strategy { get; set; } = string.Empty;

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

        [NotMapped]
        public decimal IncomeTotal { get; set; }

        [NotMapped]
        public decimal IncomePaid { get; set; }

        [NotMapped]
        public decimal IncomeOutstanding { get; set; }

        [NotMapped]
        public int IncomeCount { get; set; }

        [NotMapped]
        public decimal ExpenseTotal { get; set; }

        [NotMapped]
        public decimal ExpensePaid { get; set; }

        [NotMapped]
        public decimal ExpenseOutstanding { get; set; }

        [NotMapped]
        public int ExpenseCount { get; set; }

        [NotMapped]
        public int OpenTaskCount { get; set; }

        [NotMapped]
        public int CompletedTaskCount { get; set; }

        [NotMapped]
        public int OverdueTaskCount { get; set; }

        [NotMapped]
        public int TaskProgressPercent { get; set; }

        [NotMapped]
        public DateTime? LastTaskAt { get; set; }

        /// <summary>
        /// Дата последнего открытия карточки дела пользователем.
        /// </summary>
        public DateTime? LastViewedAt { get; set; }

        /// <summary>
        /// Пользователь, который последним открывал карточку дела.
        /// </summary>
        public int? LastViewedByUserId { get; set; }

        [MaxLength(256)]
        public string LastViewedByUserName { get; set; } = string.Empty;

        /// <summary>
        /// Дата ручного подтверждения активности по делу.
        /// </summary>
        public DateTime? ActivityConfirmedAt { get; set; }

        public int? ActivityConfirmedByUserId { get; set; }

        [MaxLength(256)]
        public string ActivityConfirmedByUserName { get; set; } = string.Empty;

        /// <summary>
        /// Включены ли напоминания о неактивности по делу.
        /// </summary>
        public bool InactivityRemindersEnabled { get; set; } = true;

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
        /// Документы, прикрепленные к делу.
        /// </summary>
        public ICollection<ClientCaseDocument> Documents { get; set; } = new List<ClientCaseDocument>();

        /// <summary>
        /// Связанные с делом лица и организации.
        /// </summary>
        public ICollection<ClientCaseRelatedParty> RelatedParties { get; set; } = new List<ClientCaseRelatedParty>();

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
