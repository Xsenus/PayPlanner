using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;

namespace PayPlanner.Api.Models
{
    /// <summary>
    ///   (  ).
    /// </summary>
    public class Payment
    {
        /// <summary>
        /// .
        /// </summary>
        public string? Account { get; set; }

        /// <summary>
        ///  .
        /// </summary>
        public DateTime? AccountDate { get; set; }

        /// <summary>
        ///   (  ).
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        ///   .
        /// </summary>
        public Client? Client { get; set; }

        /// <summary>
        ///    .
        /// </summary>
        public ClientCase? ClientCase { get; set; }

        /// <summary>
        ///     ,    .
        /// </summary>
        public int? ClientCaseId { get; set; }

        /// <summary>
        ///    .
        /// </summary>
        public int? ClientId { get; set; }

        /// <summary>
        ///      (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        ///     .
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        ///    .
        /// </summary>
        public DealType? DealType { get; set; }

        /// <summary>
        ///      (  ).
        /// </summary>
        public int? DealTypeId { get; set; }

        /// <summary>
        ///   .
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        ///   .
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        ///    .
        /// </summary>
        public IncomeType? IncomeType { get; set; }

        /// <summary>
        ///      (  ).
        /// </summary>
        public int? IncomeTypeId { get; set; }

        /// <summary>
        /// ,   .
        /// </summary>
        public bool IsPaid { get; set; } = false;

        /// <summary>
        ///    .
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        ///    ( ).
        /// </summary>
        public DateTime? PaidDate { get; set; }

        /// <summary>
        ///    .
        /// </summary>
        public PaymentSource? PaymentSource { get; set; }

        /// <summary>
        ///      (  ).
        /// </summary>
        public int? PaymentSourceId { get; set; }

        /// <summary>
        ///     .
        /// </summary>
        public PaymentStatusEntity? PaymentStatusEntity { get; set; }

        /// <summary>
        ///       (  ).
        /// </summary>
        public int? PaymentStatusId { get; set; }

        /// <summary>
        ///   (//).
        /// </summary>
        public PaymentStatus Status { get; set; }

        /// <summary>
        ///   (/).
        /// </summary>
        public PaymentType Type { get; set; }

        /// <summary>
        /// Сумма, фактически полученная по платежу.
        /// </summary>
        public decimal PaidAmount { get; set; }

        /// <summary>
        /// Изначально запланированная дата платежа.
        /// </summary>
        public DateTime? InitialDate { get; set; }

        /// <summary>
        /// Количество переносов платежа.
        /// </summary>
        public int RescheduleCount { get; set; }

        /// <summary>
        /// Системные заметки — автоматически сформированная история изменений.
        /// </summary>
        public string SystemNotes { get; set; } = string.Empty;

        /// <summary>
        /// Остаток к оплате с учётом частичных оплат.
        /// </summary>
        [NotMapped]
        public decimal OutstandingAmount
        {
            get
            {
                var amount = Amount < 0 ? 0 : Amount;
                var paid = PaidAmount < 0 ? 0 : PaidAmount;
                var remaining = amount - paid;
                return remaining > 0 ? decimal.Round(remaining, 2) : 0m;
            }
        }

        /// <summary>
        /// Признак того, что по платежу проведена частичная оплата.
        /// </summary>
        [NotMapped]
        public bool HasPartialPayment => PaidAmount > 0 && OutstandingAmount > 0;

        /// <summary>
        /// Эффективная дата отображения — фактическая дата оплаты либо текущая дата по графику.
        /// </summary>
        [NotMapped]
        public DateTime EffectiveDate => IsPaid && PaidDate.HasValue ? PaidDate.Value : Date;

        /// <summary>
        /// Число дней просрочки относительно исходной даты.
        /// </summary>
        [NotMapped]
        public int? DaysOverdue
        {
            get
            {
                if (!InitialDate.HasValue) return null;
                var planned = InitialDate.Value.Date;
                if (IsPaid && PaidDate.HasValue)
                {
                    var fact = PaidDate.Value.Date;
                    var diff = (fact - planned).Days;
                    return diff > 0 ? diff : 0;
                }

                var today = DateTime.UtcNow.Date;
                if (today <= planned) return 0;
                if (Date.Date <= planned) return (today - planned).Days;
                return null;
            }
        }

        /// <summary>
        /// Форматированное представление остатка для системных записей.
        /// </summary>
        [NotMapped]
        public string OutstandingAmountHuman => string.Format(CultureInfo.GetCultureInfo("ru-RU"), "{0:N2}", OutstandingAmount);
    }
}
