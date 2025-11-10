using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models
{
    /// <summary>
    ///     Платёж (доход или расход).
    /// </summary>
    public class Payment
    {
        /// <summary>
        ///     Номер счёта.
        /// </summary>
        public string? Account { get; set; }

        /// <summary>
        ///     Дата счёта.
        /// </summary>
        public DateTime? AccountDate { get; set; }

        /// <summary>
        ///     Плановая сумма платежа (итоговая).
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        ///     Фактически оплаченная сумма (накопительно).
        /// </summary>
        public decimal PaidAmount { get; set; }

        /// <summary>
        ///     Дата последнего фактического поступления.
        /// </summary>
        public DateTime? LastPaymentDate { get; set; }

        /// <summary>
        ///     Клиент, к которому относится платёж.
        /// </summary>
        public Client? Client { get; set; }

        /// <summary>
        ///     Кейc клиента, если привязан.
        /// </summary>
        public ClientCase? ClientCase { get; set; }

        /// <summary>
        ///     Идентификатор кейса клиента.
        /// </summary>
        public int? ClientCaseId { get; set; }

        /// <summary>
        ///     Идентификатор клиента.
        /// </summary>
        public int? ClientId { get; set; }

        /// <summary>
        ///     Момент создания записи (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        ///     Текущая плановая дата платежа.
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        ///     Изначально запланированная дата (сохраняется при переносах).
        /// </summary>
        public DateTime? OriginalDate { get; set; }

        /// <summary>
        ///     Тип сделки.
        /// </summary>
        public DealType? DealType { get; set; }

        /// <summary>
        ///     Идентификатор типа сделки.
        /// </summary>
        public int? DealTypeId { get; set; }

        /// <summary>
        ///     Описание платежа.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        ///     Уникальный идентификатор записи.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        ///     Категория дохода/расхода.
        /// </summary>
        public IncomeType? IncomeType { get; set; }

        /// <summary>
        ///     Идентификатор категории дохода/расхода.
        /// </summary>
        public int? IncomeTypeId { get; set; }

        /// <summary>
        ///     Признак полной оплаты.
        /// </summary>
        public bool IsPaid { get; set; }

        /// <summary>
        ///     Дополнительные заметки.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        ///     Дата закрытия платежа (если полностью оплачен).
        /// </summary>
        public DateTime? PaidDate { get; set; }

        /// <summary>
        ///     Источник платежа.
        /// </summary>
        public PaymentSource? PaymentSource { get; set; }

        /// <summary>
        ///     Идентификатор источника платежа.
        /// </summary>
        public int? PaymentSourceId { get; set; }

        /// <summary>
        ///     Пользовательский статус платежа.
        /// </summary>
        public PaymentStatusEntity? PaymentStatusEntity { get; set; }

        /// <summary>
        ///     Идентификатор пользовательского статуса.
        /// </summary>
        public int? PaymentStatusId { get; set; }

        /// <summary>
        ///     Статус платежа.
        /// </summary>
        public PaymentStatus Status { get; set; }

        /// <summary>
        ///     Тип платежа (доход/расход).
        /// </summary>
        public PaymentType Type { get; set; }

        /// <summary>
        ///     Сырая история событий по платежу.
        /// </summary>
        [JsonIgnore]
        public string TimelineRaw { get; set; } = "[]";

        /// <summary>
        ///     История изменений, рассчитанная из JSON.
        /// </summary>
        public IReadOnlyList<PaymentTimelineEntry> Timeline => PaymentTimelineEntry.FromJson(TimelineRaw);

        /// <summary>
        ///     Остаток к оплате.
        /// </summary>
        [NotMapped]
        public decimal OutstandingAmount => Math.Round(Math.Max(0, Amount - PaidAmount), 2, MidpointRounding.AwayFromZero);

        /// <summary>
        ///     Признак частичной оплаты.
        /// </summary>
        [NotMapped]
        public bool HasPartialPayment => PaidAmount > 0 && OutstandingAmount > 0;

        /// <summary>
        ///     Перезаписывает историю событий.
        /// </summary>
        public void ReplaceTimelineEntries(IEnumerable<PaymentTimelineEntry> entries)
        {
            TimelineRaw = PaymentTimelineEntry.ToJson(entries);
        }

        /// <summary>
        ///     Возвращает историю событий.
        /// </summary>
        public IReadOnlyList<PaymentTimelineEntry> GetTimelineEntries() => Timeline.ToList();

        /// <summary>
        ///     Добавляет событие в историю платежа.
        /// </summary>
        public void AppendTimelineEntry(PaymentTimelineEntry entry)
        {
            if (entry is null) return;
            var list = Timeline.ToList();
            list.Add(entry);
            ReplaceTimelineEntries(list);
        }
    }
}
