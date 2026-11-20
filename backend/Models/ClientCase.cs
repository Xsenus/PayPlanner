using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

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

        /// <summary>Наша роль в судебном деле: claimant, respondent или third-party. Null — не указана.</summary>
        [MaxLength(24)]
        public string? OurCourtRole { get; set; }

        /// <summary>Заявленная в суде сумма требований, не гонорар по договору.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? CourtClaimAmount { get; set; }

        /// <summary>Сумма требований, присуждённая судом; направление взыскания определяется ролью в деле.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? CourtAwardedAmount { get; set; }

        /// <summary>Указанная сотрудником сумма госпошлины по делу.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? CourtStateFeeAmount { get; set; }

        [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ClearCourtClaimAmount { get; set; }
        [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ClearCourtAwardedAmount { get; set; }
        [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ClearCourtStateFeeAmount { get; set; }

        /// <summary>
        /// Номер дела, присвоенный судом.
        /// </summary>
        [MaxLength(100)]
        public string CourtCaseNumber { get; set; } = string.Empty;

        /// <summary>
        /// Неизменяемый идентификатор судебного дела, объединяющий номера разных инстанций.
        /// </summary>
        [MaxLength(120)]
        public string JudicialUid { get; set; } = string.Empty;

        /// <summary>
        /// Ссылка на карточку дела на сайте суда.
        /// </summary>
        [MaxLength(1000)]
        public string CourtUrl { get; set; } = string.Empty;

        /// <summary>Включён ли автоматический опрос судебного источника для этого дела.</summary>
        public bool CourtSyncMonitoringEnabled { get; set; } = true;

        /// <summary>Последняя попытка автоматической или ручной внешней синхронизации (UTC).</summary>
        public DateTime? CourtSyncLastAttemptAt { get; set; }

        /// <summary>Последняя успешная внешняя синхронизация (UTC).</summary>
        public DateTime? CourtSyncLastSuccessAt { get; set; }

        /// <summary>Начало текущей непрерывной серии ошибок синхронизации (UTC).</summary>
        public DateTime? CourtSyncFailureStartedAt { get; set; }

        /// <summary>Причина последней ошибки внешней синхронизации.</summary>
        [MaxLength(1000)]
        public string CourtSyncLastError { get; set; } = string.Empty;

        /// <summary>
        /// Дата ближайшего судебного заседания.
        /// </summary>
        public DateTime? NextHearingAt { get; set; }

        /// <summary>Дата заседания, которую сотрудник сверил после предыдущего заседания.</summary>
        public DateTime? NextHearingConfirmedForAt { get; set; }
        public DateTime? NextHearingConfirmedAt { get; set; }
        public int? NextHearingConfirmedByUserId { get; set; }
        [MaxLength(256)]
        public string NextHearingConfirmedByName { get; set; } = string.Empty;

        /// <summary>
        /// Истец/заявитель по делу.
        /// </summary>
        [MaxLength(300)]
        public string Claimant { get; set; } = string.Empty;

        [MaxLength(12)]
        public string? ClaimantInn { get; set; }

        [MaxLength(15)]
        public string? ClaimantOgrn { get; set; }

        /// <summary>
        /// Ответчик/должник по делу.
        /// </summary>
        [MaxLength(300)]
        public string Defendant { get; set; } = string.Empty;

        [MaxLength(12)]
        public string? DefendantInn { get; set; }

        [MaxLength(15)]
        public string? DefendantOgrn { get; set; }

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

        /// <summary>
        /// Дата регистрации заявления судом; не подменяется датой подачи или создания карточки CRM.
        /// </summary>
        public DateTime? CourtRegisteredAt { get; set; }

        [NotMapped]
        public bool ClearCourtRegisteredAt { get; set; }

        public DateTime? CourtDecisionAt { get; set; }

        /// <summary>
        /// Указанная сотрудником дата вступления судебного решения в силу.
        /// Не вычисляется автоматически из даты решения.
        /// </summary>
        public DateTime? CourtDecisionEffectiveAt { get; set; }

        [NotMapped]
        public bool ClearCourtDecisionEffectiveAt { get; set; }

        /// <summary>
        /// Указанная сотрудником дата фактического получения исполнительного листа.
        /// </summary>
        public DateTime? ExecutiveWritReceivedAt { get; set; }

        [NotMapped]
        public bool ClearExecutiveWritReceivedAt { get; set; }

        /// <summary>Дата фактической передачи исполнительного листа на исполнение.</summary>
        public DateTime? ExecutiveWritSubmittedAt { get; set; }

        /// <summary>Адресат передачи: fssp или bank.</summary>
        [MaxLength(10)]
        public string? ExecutiveWritSubmittedTo { get; set; }

        [NotMapped]
        public bool ClearExecutiveWritSubmission { get; set; }

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

        /// <summary>
        /// Explicit review of the current case position. Editing Strategy clears the review.
        /// </summary>
        public DateTime? PositionReviewedAt { get; set; }
        public int? PositionReviewedByUserId { get; set; }
        [MaxLength(256)]
        public string PositionReviewedByName { get; set; } = string.Empty;

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
        public bool? HasCompletePaymentVisibility { get; set; }

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
        public string TaskSummaryVisibility { get; set; } = "none";

        [NotMapped]
        public DateTime? LastTaskAt { get; set; }

        [NotMapped]
        public DateTime? LastMeaningfulActivityAt { get; set; }

        [NotMapped]
        public DateTime? NextInactivityControlAt { get; set; }

        [NotMapped]
        public string InactivityControlExclusionReason { get; set; } = string.Empty;

        /// <summary>
        /// Дата последнего открытия карточки дела пользователем.
        /// </summary>
        public DateTime? LastViewedAt { get; set; }

        [NotMapped]
        public DateTime? LastViewedByResponsibleAt { get; set; }

        [NotMapped]
        public int? LastViewedByResponsibleUserId { get; set; }

        [NotMapped]
        public string LastViewedByResponsibleUserName { get; set; } = string.Empty;

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
        /// Причина индивидуального исключения дела из автоконтроля.
        /// </summary>
        [MaxLength(1000)]
        public string InactivityRemindersDisabledReason { get; set; } = string.Empty;

        public bool IsUnderControl { get; set; }

        public DateTime? ControlMarkedAt { get; set; }

        public int? ControlMarkedByUserId { get; set; }

        public User? ControlMarkedByUser { get; set; }

        [MaxLength(256)]
        public string ControlMarkedByUserName { get; set; } = string.Empty;

        /// <summary>
        /// Дело скрыто от исполнителей с ограниченной видимостью и доступно только ролям с полным обзором.
        /// </summary>
        public bool IsHiddenFromExecutor { get; set; }

        public DateTime? HiddenFromExecutorAt { get; set; }

        public int? HiddenFromExecutorByUserId { get; set; }

        [MaxLength(256)]
        public string HiddenFromExecutorByUserName { get; set; } = string.Empty;

        public DateTime? AccessRequestedAt { get; set; }

        public int? AccessRequestedByUserId { get; set; }

        [MaxLength(256)]
        public string AccessRequestedByUserName { get; set; } = string.Empty;

        [MaxLength(1000)]
        public string AccessRequestReason { get; set; } = string.Empty;

        [NotMapped]
        public bool CanRevealSensitiveData { get; set; } = true;

        /// <summary>
        /// Краткий перечень заполненных судебных инстанций для карточного реестра.
        /// </summary>
        [NotMapped]
        public List<string> CourtInstanceKeys { get; set; } = new();

        [NotMapped]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HasStoredCourtInstanceBinding { get; set; }

        /// <summary>Подтверждённый результат именно отображаемого в реестре заседания.</summary>
        [NotMapped]
        public string? NextHearingOutcome { get; set; }

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
        /// Договоры, напрямую связанные с делом.
        /// </summary>
        public ICollection<ClientCaseContract> ClientCaseContracts { get; set; } = new List<ClientCaseContract>();

        /// <summary>
        /// Документы, прикрепленные к делу.
        /// </summary>
        public ICollection<ClientCaseDocument> Documents { get; set; } = new List<ClientCaseDocument>();

        /// <summary>
        /// Связанные с делом лица и организации.
        /// </summary>
        public ICollection<ClientCaseRelatedParty> RelatedParties { get; set; } = new List<ClientCaseRelatedParty>();

        /// <summary>
        /// Предварительные расчеты / заказ-наряды по делу.
        /// </summary>
        public ICollection<ClientCaseWorkOrder> WorkOrders { get; set; } = new List<ClientCaseWorkOrder>();

        public ICollection<ClientCaseProceduralDeadline> ProceduralDeadlines { get; set; } = new List<ClientCaseProceduralDeadline>();

        public ICollection<ClientCaseCourtEvent> CourtEvents { get; set; } = new List<ClientCaseCourtEvent>();

        public ICollection<ClientCaseHearingPreparation> HearingPreparations { get; set; } = new List<ClientCaseHearingPreparation>();

        public ICollection<ClientCaseCourtParty> CourtParties { get; set; } = new List<ClientCaseCourtParty>();

        public ICollection<ClientCaseCourtEventHistory> CourtEventHistory { get; set; } = new List<ClientCaseCourtEventHistory>();

        public ICollection<ClientCaseCourtInstance> CourtInstances { get; set; } = new List<ClientCaseCourtInstance>();

        public ICollection<ClientCaseCourtComplaint> CourtComplaints { get; set; } = new List<ClientCaseCourtComplaint>();

        public ICollection<ClientCaseAppealDecision> AppealDecisions { get; set; } = new List<ClientCaseAppealDecision>();

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
