namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Сущность платежа (приход или расход).
    /// </summary>
    public class Payment
    {
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
        /// Внешний ключ на дело клиента, к которому относится платеж.
        /// </summary>
        public int? ClientCaseId { get; set; }

        /// <summary>
        /// Внешний ключ на клиента.
        /// </summary>
        public int? ClientId { get; set; }

        /// <summary>
        /// Дата и время создания записи (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Плановая или фактическая дата платежа.
        /// </summary>
        public DateTime Date { get; set; }

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
        /// Признак, что платеж оплачен.
        /// </summary>
        public bool IsPaid { get; set; } = false;

        /// <summary>
        /// Произвольные заметки по платежу.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Дата фактической оплаты (если оплачено).
        /// </summary>
        public DateTime? PaidDate { get; set; }

        /// <summary>
        /// Навигация на источник платежа.
        /// </summary>
        public PaymentSource? PaymentSource { get; set; }

        /// <summary>
        /// Внешний ключ на источник платежа (если используется справочник).
        /// </summary>
        public int? PaymentSourceId { get; set; }

        /// <summary>
        /// Навигация на сущность статуса платежа.
        /// </summary>
        public PaymentStatusEntity? PaymentStatusEntity { get; set; }

        /// <summary>
        /// Внешний ключ на сущность статуса платежа (если используется справочник).
        /// </summary>
        public int? PaymentStatusId { get; set; }

        /// <summary>
        /// Статус платежа (ожидается/выполнен/просрочен).
        /// </summary>
        public PaymentStatus Status { get; set; }

        /// <summary>
        /// Тип платежа (приход/расход).
        /// </summary>
        public PaymentType Type { get; set; }
    }
}
