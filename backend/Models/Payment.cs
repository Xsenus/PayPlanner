using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Сущность платежа (приход или расход).
    /// </summary>
    public class Payment
    {
        /// <summary>
        /// Счет.
        /// </summary>
        public string? Account { get; set; }

        /// <summary>
        /// Дата счета.
        /// </summary>
        public DateTime? AccountDate { get; set; }

        /// <summary>
        /// Внешний ключ на счёт.
        /// </summary>
        public int? InvoiceId { get; set; }
        public string? LegacyAccountNumber { get; set; }
        public DateTime? LegacyAccountDate { get; set; }

        /// <summary>
        /// Навигация на счёт.
        /// </summary>
        public Invoice? Invoice { get; set; }

        /// <summary>
        /// Акты, явно привязанные к счёту.
        /// </summary>
        public ICollection<Act> Acts { get; set; } = new List<Act>();

        /// <summary>
        /// Сумма платежа (в валюте учёта).
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// Навигация на клиента.
        /// </summary>
        public Client? Client { get; set; }

        /// <summary>
        /// Навигация на дело клиента.
        /// </summary>
        public ClientCase? ClientCase { get; set; }

        /// <summary>
        /// Явные связи счёта с несколькими делами клиента.
        /// </summary>
        public ICollection<InvoiceClientCase> InvoiceClientCases { get; set; } = new List<InvoiceClientCase>();

        public ICollection<InvoiceLineItem> InvoiceLineItems { get; set; } = new List<InvoiceLineItem>();

        /// <summary>
        /// Последний выбранный печатный шаблон счета. Используется связанными документами,
        /// чтобы различать обычный счет и счет-договор.
        /// </summary>
        public string? InvoiceDocumentTemplateKey { get; set; }

        public ICollection<CashOrder> CashOrders { get; set; } = new List<CashOrder>();

        public string InvoiceWorkflowStatus { get; set; } = PayPlanner.Api.Models.InvoiceWorkflowStatus.Draft;

        public DateTime? InvoiceSentForSigningAt { get; set; }

        public DateTime? InvoiceSignedAt { get; set; }

        public DateTime? InvoiceCancelledAt { get; set; }

        public int? InvoiceCancelledByUserId { get; set; }

        public string? InvoiceCancelledReason { get; set; }

        /// <summary>
        /// Внешний ключ на дело клиента, к которому относится платеж.
        /// </summary>
        public int? ClientCaseId { get; set; }

        /// <summary>
        /// Внешний ключ на клиента.
        /// </summary>
        public int? ClientId { get; set; }

        /// <summary>
        /// Внешний ключ на юридическое лицо-контрагента.
        /// </summary>
        public int? LegalEntityId { get; set; }

        /// <summary>
        /// Навигация на юридическое лицо-контрагента.
        /// </summary>
        public LegalEntity? LegalEntity { get; set; }

        /// <summary>
        /// Клиент, к которому относится расходный счет. Для доходных счетов обычно не заполняется.
        /// </summary>
        public int? ExpenseClientId { get; set; }

        /// <summary>
        /// Юридическое лицо, к которому относится расходный счет.
        /// </summary>
        public int? ExpenseLegalEntityId { get; set; }

        /// <summary>
        /// Навигация на юридическое лицо, к которому относится расходный счет.
        /// </summary>
        public LegalEntity? ExpenseLegalEntity { get; set; }

        /// <summary>
        /// Навигация на клиента, к которому относится расходный счет.
        /// </summary>
        public Client? ExpenseClient { get; set; }

        /// <summary>
        /// Каноническая ссылка на договор (если документ относится к договору).
        /// </summary>
        public int? ContractId { get; set; }

        /// <summary>
        /// Навигация на договор.
        /// </summary>
        public Contract? Contract { get; set; }

        /// <summary>
        /// Тип документа в общем платежном реестре.
        /// </summary>
        public DocumentKind DocumentKind { get; set; } = DocumentKind.Payment;

        /// <summary>
        /// Дата и время создания записи (UTC).
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
        /// Текущая плановая дата платежа (используется в календаре и аналитике).
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// Исходная дата, когда платеж был запланирован изначально.
        /// </summary>
        public DateTime PlannedDate { get; set; }

        /// <summary>
        /// Навигация на тип сделки.
        /// </summary>
        public DealType? DealType { get; set; }

        /// <summary>
        /// Внешний ключ на тип сделки (если используется справочник).
        /// </summary>
        public int? DealTypeId { get; set; }

        /// <summary>
        /// Короткое описание платежа.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Название контрагента, от которого поступил или которому отправлен счёт.
        /// </summary>
        public string? CounterpartyName { get; set; }

        [NotMapped]
        public int? ContractorUserId { get; set; }

        [NotMapped]
        public string? ContractorUserName { get; set; }

        /// <summary>
        /// Уникальный идентификатор платежа.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Навигация на тип дохода.
        /// </summary>
        public IncomeType? IncomeType { get; set; }

        /// <summary>
        /// Внешний ключ на тип дохода (если используется справочник).
        /// </summary>
        public int? IncomeTypeId { get; set; }

        /// <summary>
        /// Признак, что платеж оплачен полностью.
        /// </summary>
        public bool IsPaid { get; set; }

        /// <summary>
        /// Фактически оплаченная сумма (может быть меньше заявленной при частичном платеже).
        /// </summary>
        public decimal PaidAmount { get; set; }

        public PaymentAdvanceReviewStatus AdvanceReviewStatus { get; set; } = PaymentAdvanceReviewStatus.None;

        public string? AdvanceReviewReason { get; set; }

        public DateTime? AdvanceReviewedAt { get; set; }

        public int? AdvanceReviewedByUserId { get; set; }

        public string? AdvanceReviewedByUserName { get; set; }

        [NotMapped]
        public PaymentOverpaymentHandling? OverpaymentHandling { get; set; }

        [NotMapped]
        public int? OverpaymentTargetPaymentId { get; set; }

        [NotMapped]
        public string? OverpaymentComment { get; set; }

        /// <summary>
        /// Дата последнего поступления денег по этому платежу.
        /// </summary>
        public DateTime? LastPaymentDate { get; set; }

        /// <summary>
        /// Произвольные заметки по платежу.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Признак, что счет носит сервисный характер и не предназначен для бухгалтерии.
        /// </summary>
        public bool IsNotForAccounting { get; set; }

        public string? ClientCaseReference { get; set; }

        /// <summary>
        /// Системные заметки, автоматически формируемые приложением (история переносов и т. п.).
        /// </summary>
        public string SystemNotes { get; set; } = string.Empty;

        /// <summary>
        /// Количество переносов платежа на новую дату.
        /// </summary>
        public int RescheduleCount { get; set; }

        /// <summary>
        /// Дата фактической оплаты (если оплачено полностью).
        /// </summary>
        public DateTime? PaidDate { get; set; }

        /// <summary>
        /// Номер платёжного документа (платёжное поручение, ордер и т. п.).
        /// </summary>
        public string? PaymentOrderNumber { get; set; }

        /// <summary>
        /// Дата платёжного документа.
        /// </summary>
        public DateTime? PaymentDocumentDate { get; set; }

        /// <summary>
        /// Навигация на источник платежа.
        /// </summary>
        public PaymentSource? PaymentSource { get; set; }

        /// <summary>
        /// Навигация на услугу из справочника услуг.
        /// </summary>
        public ServiceDictionaryItem? Service { get; set; }

        /// <summary>
        /// Внешний ключ на источник платежа (если используется справочник).
        /// </summary>
        public int? PaymentSourceId { get; set; }

        /// <summary>
        /// Внешний ключ на услугу из справочника услуг.
        /// </summary>
        public int? ServiceId { get; set; }

        /// <summary>
        /// Навигация на сущность статуса платежа.
        /// </summary>
        public PaymentStatusEntity? PaymentStatusEntity { get; set; }

        /// <summary>
        /// Внешний ключ на сущность статуса платежа (если используется справочник).
        /// </summary>
        public int? PaymentStatusId { get; set; }

        /// <summary>
        /// Назначенные ответственные пользователи по счёту.
        /// </summary>
        public ICollection<PaymentResponsible> Responsibles { get; set; } = new List<PaymentResponsible>();

        /// <summary>
        /// Статус платежа (ожидается/выполнен/просрочен и т. д.).
        /// </summary>
        public PaymentStatus Status { get; set; }

        /// <summary>
        /// Тип платежа (приход/расход).
        /// </summary>
        public PaymentType Type { get; set; }

        /// <summary>
        /// Остаток к оплате по платежу.
        /// </summary>
        [NotMapped]
        public decimal OutstandingAmount => Amount > PaidAmount
            ? Math.Round(Amount - PaidAmount, 2)
            : 0m;

        /// <summary>
        /// Признак частично оплаченного платежа.
        /// </summary>
        [NotMapped]
        public bool HasPartialPayment => PaidAmount > 0m && PaidAmount < Amount;

        /// <summary>
        /// Количество дней просрочки относительно изначальной даты (если есть фактическая оплата).
        /// </summary>
        [NotMapped]
        public int? DelayDays
        {
            get
            {
                if (!LastPaymentDate.HasValue) return null;
                var delay = (LastPaymentDate.Value.Date - PlannedDate.Date).TotalDays;
                return (int)Math.Round(delay);
            }
        }
    }
}
