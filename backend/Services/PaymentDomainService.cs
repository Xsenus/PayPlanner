using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    ///     Бизнес-логика обработки платежей: пересчёт статусов, сумм и истории событий.
    /// </summary>
    public static class PaymentDomainService
    {
        private const decimal Tolerance = 0.01m;

        /// <summary>
        ///     Подготавливает платёж к сохранению при создании.
        /// </summary>
        public static void PrepareForCreate(Payment entity)
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));

            NormalizeMonetaryFields(entity);
            entity.OriginalDate ??= entity.Date.Date;
            entity.Date = entity.Date.Date;
            entity.AccountDate = entity.AccountDate?.Date;
            entity.PaidDate = entity.PaidDate?.Date;
            entity.LastPaymentDate = entity.LastPaymentDate?.Date;

            if (entity.PaidAmount > 0 && !entity.LastPaymentDate.HasValue)
            {
                entity.LastPaymentDate = entity.PaidDate ?? entity.AccountDate ?? entity.Date;
            }

            var statusBefore = entity.Status;
            ApplyStatus(entity, entity.Status);

            var history = new List<PaymentTimelineEntry>
            {
                PaymentTimelineEntry.Created(DateTime.UtcNow, entity.OriginalDate ?? entity.Date, entity.Amount)
            };

            if (entity.PaidAmount > 0)
            {
                history.Add(
                    PaymentTimelineEntry.Partial(
                        DateTime.UtcNow,
                        entity.PaidAmount,
                        entity.PaidAmount,
                        entity.OutstandingAmount,
                        entity.LastPaymentDate ?? entity.PaidDate ?? entity.Date,
                        "Первичное поступление"));

                if (entity.OutstandingAmount <= Tolerance)
                {
                    history.Add(PaymentTimelineEntry.Finalized(
                        DateTime.UtcNow,
                        entity.PaidAmount,
                        entity.OutstandingAmount,
                        entity.LastPaymentDate ?? entity.PaidDate ?? entity.Date));
                }
            }

            if (statusBefore != entity.Status)
            {
                history.Add(PaymentTimelineEntry.StatusChanged(DateTime.UtcNow, statusBefore, entity.Status, entity.OutstandingAmount));
            }

            entity.ReplaceTimelineEntries(history);
        }

        /// <summary>
        ///     Применяет обновления из <paramref name="model"/> к существующей сущности.
        /// </summary>
        public static void ApplyUpdate(Payment entity, Payment model)
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));
            if (model is null) throw new ArgumentNullException(nameof(model));

            var now = DateTime.UtcNow;
            var previousAmount = entity.Amount;
            var previousPaidAmount = entity.PaidAmount;
            var previousDate = entity.Date;
            var previousStatus = entity.Status;
            var previousOutstanding = entity.OutstandingAmount;

            entity.Amount = Round(model.Amount);
            entity.PaidAmount = Math.Max(0, Round(model.PaidAmount));
            entity.Description = model.Description;
            entity.Notes = model.Notes;
            entity.Type = model.Type;
            entity.ClientId = model.ClientId;
            entity.ClientCaseId = model.ClientCaseId;
            entity.DealTypeId = model.DealTypeId;
            entity.IncomeTypeId = model.IncomeTypeId;
            entity.PaymentSourceId = model.PaymentSourceId;
            entity.PaymentStatusId = model.PaymentStatusId;
            entity.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
            entity.AccountDate = model.AccountDate?.Date;
            entity.Date = model.Date.Date;
            entity.PaidDate = model.PaidDate?.Date;
            entity.LastPaymentDate = model.LastPaymentDate?.Date;
            entity.OriginalDate ??= model.OriginalDate?.Date ?? previousDate.Date;

            if (entity.PaidAmount > 0 && !entity.LastPaymentDate.HasValue)
            {
                entity.LastPaymentDate = entity.PaidDate ?? entity.AccountDate ?? entity.Date;
            }

            if (entity.PaidAmount > entity.Amount && entity.Amount > 0)
            {
                entity.PaidAmount = entity.Amount;
            }

            var history = entity.GetTimelineEntries().ToList();

            if (previousAmount != entity.Amount)
            {
                history.Add(PaymentTimelineEntry.AmountAdjusted(now, previousAmount, entity.Amount, entity.PaidAmount, entity.OutstandingAmount));
            }

            var delta = entity.PaidAmount - previousPaidAmount;
            if (Math.Abs(delta) > Tolerance)
            {
                history.Add(PaymentTimelineEntry.Partial(
                    now,
                    delta,
                    entity.PaidAmount,
                    entity.OutstandingAmount,
                    entity.LastPaymentDate ?? entity.PaidDate ?? now,
                    delta > 0 ? null : "Коррекция оплаты"));
            }

            if (previousDate.Date != entity.Date.Date)
            {
                history.Add(PaymentTimelineEntry.Rescheduled(now, previousDate, entity.Date, entity.OutstandingAmount));
            }

            var statusBefore = entity.Status;
            var computedStatus = ApplyStatus(entity, model.Status);
            if (statusBefore != computedStatus)
            {
                history.Add(PaymentTimelineEntry.StatusChanged(now, statusBefore, computedStatus, entity.OutstandingAmount));
            }

            if (entity.OutstandingAmount <= Tolerance && previousOutstanding > Tolerance)
            {
                history.Add(PaymentTimelineEntry.Finalized(now, entity.PaidAmount, entity.OutstandingAmount, entity.LastPaymentDate ?? entity.PaidDate ?? entity.Date));
            }

            entity.ReplaceTimelineEntries(history);
        }

        private static void NormalizeMonetaryFields(Payment entity)
        {
            entity.Amount = Round(entity.Amount);
            entity.PaidAmount = Math.Max(0, Round(entity.PaidAmount));
            if (entity.Amount < 0) entity.Amount = 0;
            if (entity.Amount > 0 && entity.PaidAmount > entity.Amount) entity.PaidAmount = entity.Amount;
        }

        private static PaymentStatus ApplyStatus(Payment entity, PaymentStatus requestedStatus)
        {
            var outstanding = entity.OutstandingAmount;
            if (outstanding <= Tolerance)
            {
                entity.IsPaid = true;
                entity.Status = PaymentStatus.Completed;
                entity.PaidAmount = entity.Amount;
                entity.PaidDate ??= entity.LastPaymentDate ?? entity.AccountDate ?? entity.Date;
                entity.LastPaymentDate ??= entity.PaidDate;
            }
            else
            {
                entity.IsPaid = false;
                entity.PaidDate = null;

                if (requestedStatus is PaymentStatus.Cancelled or PaymentStatus.Processing)
                {
                    entity.Status = requestedStatus;
                }
                else
                {
                    entity.Status = entity.Date.Date < DateTime.UtcNow.Date
                        ? PaymentStatus.Overdue
                        : PaymentStatus.Pending;
                }
            }

            return entity.Status;
        }

        private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}
