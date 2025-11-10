using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models
{
    /// <summary>
    ///     Тип события в истории платежа.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PaymentTimelineEventType
    {
        /// <summary>
        ///     Платёж создан.
        /// </summary>
        Created,

        /// <summary>
        ///     Зафиксировано поступление средств (частичное или окончательное).
        /// </summary>
        PartialPayment,

        /// <summary>
        ///     Изменена плановая сумма платежа.
        /// </summary>
        AmountAdjusted,

        /// <summary>
        ///     Сдвинута плановая дата платежа.
        /// </summary>
        Rescheduled,

        /// <summary>
        ///     Изменён статус платежа.
        /// </summary>
        StatusChanged,

        /// <summary>
        ///     Платёж полностью закрыт.
        /// </summary>
        Finalized
    }

    /// <summary>
    ///     Запись в истории платежа. Используется для отображения и аналитики изменений.
    /// </summary>
    public class PaymentTimelineEntry
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        /// <summary>
        ///     Момент фиксации события (UTC).
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        ///     Тип события.
        /// </summary>
        public PaymentTimelineEventType EventType { get; set; }

        /// <summary>
        ///     Изменение суммы, связанное с событием (для поступлений).
        /// </summary>
        public decimal? AmountDelta { get; set; }

        /// <summary>
        ///     Дата, к которой относится событие (дата платежа или новая плановая дата).
        /// </summary>
        public DateTime? EffectiveDate { get; set; }

        /// <summary>
        ///     Предыдущая плановая дата (для переноса).
        /// </summary>
        public DateTime? PreviousDate { get; set; }

        /// <summary>
        ///     Новая плановая дата (для переноса).
        /// </summary>
        public DateTime? NewDate { get; set; }

        /// <summary>
        ///     Предыдущее значение плановой суммы.
        /// </summary>
        public decimal? PreviousAmount { get; set; }

        /// <summary>
        ///     Новое значение плановой суммы.
        /// </summary>
        public decimal? NewAmount { get; set; }

        /// <summary>
        ///     Итоговая сумма оплаченных средств после события.
        /// </summary>
        public decimal? TotalPaid { get; set; }

        /// <summary>
        ///     Остаток к оплате после события.
        /// </summary>
        public decimal? Outstanding { get; set; }

        /// <summary>
        ///     Предыдущий статус платежа.
        /// </summary>
        public PaymentStatus? PreviousStatus { get; set; }

        /// <summary>
        ///     Новый статус платежа.
        /// </summary>
        public PaymentStatus? NewStatus { get; set; }

        /// <summary>
        ///     Дополнительный комментарий.
        /// </summary>
        public string? Comment { get; set; }

        /// <summary>
        ///     Создает запись о создании платежа.
        /// </summary>
        public static PaymentTimelineEntry Created(DateTime timestampUtc, DateTime? plannedDate, decimal amount)
            => new()
            {
                Timestamp = EnsureUtc(timestampUtc),
                EventType = PaymentTimelineEventType.Created,
                EffectiveDate = plannedDate?.Date,
                NewAmount = Normalize(amount),
                Outstanding = Normalize(amount)
            };

        /// <summary>
        ///     Создает запись о поступлении средств.
        /// </summary>
        public static PaymentTimelineEntry Partial(DateTime timestampUtc, decimal delta, decimal totalPaid, decimal outstanding, DateTime? paymentDate, string? comment = null)
            => new()
            {
                Timestamp = EnsureUtc(timestampUtc),
                EventType = PaymentTimelineEventType.PartialPayment,
                AmountDelta = Normalize(delta),
                TotalPaid = Normalize(totalPaid),
                Outstanding = Normalize(outstanding),
                EffectiveDate = paymentDate?.Date,
                Comment = comment
            };

        /// <summary>
        ///     Создает запись об изменении плановой суммы.
        /// </summary>
        public static PaymentTimelineEntry AmountAdjusted(DateTime timestampUtc, decimal previousAmount, decimal newAmount, decimal totalPaid, decimal outstanding)
            => new()
            {
                Timestamp = EnsureUtc(timestampUtc),
                EventType = PaymentTimelineEventType.AmountAdjusted,
                PreviousAmount = Normalize(previousAmount),
                NewAmount = Normalize(newAmount),
                TotalPaid = Normalize(totalPaid),
                Outstanding = Normalize(outstanding)
            };

        /// <summary>
        ///     Создает запись о переносе платежа.
        /// </summary>
        public static PaymentTimelineEntry Rescheduled(DateTime timestampUtc, DateTime previousDate, DateTime newDate, decimal outstanding, string? comment = null)
            => new()
            {
                Timestamp = EnsureUtc(timestampUtc),
                EventType = PaymentTimelineEventType.Rescheduled,
                PreviousDate = previousDate.Date,
                NewDate = newDate.Date,
                Outstanding = Normalize(outstanding),
                Comment = comment
            };

        /// <summary>
        ///     Создает запись об изменении статуса платежа.
        /// </summary>
        public static PaymentTimelineEntry StatusChanged(DateTime timestampUtc, PaymentStatus previousStatus, PaymentStatus newStatus, decimal outstanding, string? comment = null)
            => new()
            {
                Timestamp = EnsureUtc(timestampUtc),
                EventType = PaymentTimelineEventType.StatusChanged,
                PreviousStatus = previousStatus,
                NewStatus = newStatus,
                Outstanding = Normalize(outstanding),
                Comment = comment
            };

        /// <summary>
        ///     Создает запись о полном закрытии платежа.
        /// </summary>
        public static PaymentTimelineEntry Finalized(DateTime timestampUtc, decimal totalPaid, decimal outstanding, DateTime? paymentDate)
            => new()
            {
                Timestamp = EnsureUtc(timestampUtc),
                EventType = PaymentTimelineEventType.Finalized,
                TotalPaid = Normalize(totalPaid),
                Outstanding = Normalize(outstanding),
                EffectiveDate = paymentDate?.Date
            };

        /// <summary>
        ///     Десериализует историю из JSON.
        /// </summary>
        public static IReadOnlyList<PaymentTimelineEntry> FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PaymentTimelineEntry>();
            try
            {
                var parsed = JsonSerializer.Deserialize<List<PaymentTimelineEntry>>(json, SerializerOptions) ?? new List<PaymentTimelineEntry>();
                parsed.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                return parsed;
            }
            catch
            {
                return Array.Empty<PaymentTimelineEntry>();
            }
        }

        /// <summary>
        ///     Сериализует историю в JSON.
        /// </summary>
        public static string ToJson(IEnumerable<PaymentTimelineEntry> entries)
        {
            var list = (entries ?? Enumerable.Empty<PaymentTimelineEntry>())
                .Where(e => e is not null)
                .Select(e =>
                {
                    e.Timestamp = EnsureUtc(e.Timestamp);
                    if (e.EffectiveDate.HasValue) e.EffectiveDate = e.EffectiveDate.Value.Date;
                    if (e.PreviousDate.HasValue) e.PreviousDate = e.PreviousDate.Value.Date;
                    if (e.NewDate.HasValue) e.NewDate = e.NewDate.Value.Date;
                    return e;
                })
                .OrderBy(e => e.Timestamp)
                .ToList();
            return JsonSerializer.Serialize(list, SerializerOptions);
        }

        private static decimal Normalize(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private static DateTime EnsureUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => value,
            };
        }
    }
}
