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

        /// <summary>
        /// Дата последнего поступления денег по этому платежу.
        /// </summary>
        public DateTime? LastPaymentDate { get; set; }

        /// <summary>
        /// Произвольные заметки по платежу.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

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
