using System;
using System.Globalization;
using System.Text;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions;

internal static class PaymentBusinessLogic
{
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    public static void PrepareForCreate(Payment model, DateTime nowUtc)
    {
        model.InitialDate ??= model.Date;
        model.RescheduleCount = model.RescheduleCount < 0 ? 0 : model.RescheduleCount;
        model.SystemNotes = string.Empty;
        model.PaidAmount = NormalizePaidAmount(model.PaidAmount, model.Amount);

        if (model.Status is PaymentStatus.Cancelled or PaymentStatus.Processing)
        {
            model.IsPaid = false;
            model.PaidDate = null;
            return;
        }

        if (model.IsPaid || model.PaidAmount >= model.Amount && model.Amount > 0)
        {
            MarkAsCompleted(model, nowUtc, model.PaidDate ?? model.Date);
            return;
        }

        model.IsPaid = false;
        model.PaidDate = null;
        model.Status = model.Date.Date < nowUtc.Date ? PaymentStatus.Overdue : PaymentStatus.Pending;
    }

    public static void ApplyUpdate(Payment entity, Payment incoming, DateTime nowUtc)
    {
        var previousDate = entity.Date;
        var previousPaidAmount = entity.PaidAmount;
        var previousStatus = entity.Status;
        var previousIsPaid = entity.IsPaid;
        var previousPaidDate = entity.PaidDate;
        var previousInitial = entity.InitialDate;
        var previousNotes = entity.SystemNotes ?? string.Empty;
        var previousReschedules = entity.RescheduleCount;

        entity.Amount = incoming.Amount;
        entity.Type = incoming.Type;
        entity.Description = incoming.Description ?? string.Empty;
        entity.Notes = incoming.Notes ?? string.Empty;
        entity.ClientId = incoming.ClientId;
        entity.ClientCaseId = incoming.ClientCaseId;
        entity.DealTypeId = incoming.DealTypeId;
        entity.IncomeTypeId = incoming.IncomeTypeId;
        entity.PaymentSourceId = incoming.PaymentSourceId;
        entity.PaymentStatusId = incoming.PaymentStatusId;
        entity.Account = string.IsNullOrWhiteSpace(incoming.Account) ? null : incoming.Account.Trim();
        entity.AccountDate = incoming.AccountDate;
        entity.InitialDate = incoming.InitialDate ?? previousInitial ?? entity.Date;
        entity.PaidAmount = NormalizePaidAmount(incoming.PaidAmount, entity.Amount);
        entity.Date = incoming.Date;

        if (incoming.Status is PaymentStatus.Cancelled or PaymentStatus.Processing)
        {
            entity.Status = incoming.Status;
            entity.IsPaid = false;
            entity.PaidDate = null;
        }
        else if (incoming.IsPaid || entity.PaidAmount >= entity.Amount && entity.Amount > 0)
        {
            MarkAsCompleted(entity, nowUtc, incoming.PaidDate ?? entity.PaidDate ?? entity.Date);
        }
        else
        {
            entity.IsPaid = false;
            entity.PaidDate = null;
            entity.Status = entity.Date.Date < nowUtc.Date ? PaymentStatus.Overdue : PaymentStatus.Pending;
        }

        var sb = new StringBuilder(previousNotes.Trim());
        bool notesChanged = false;

        if (entity.PaidAmount > previousPaidAmount && entity.PaidAmount < entity.Amount)
        {
            AppendLine(sb,
                $"Получено {FormatMoney(entity.PaidAmount - previousPaidAmount)} (всего {FormatMoney(entity.PaidAmount)} из {FormatMoney(entity.Amount)}). Остаток {FormatMoney(entity.OutstandingAmount)} к {entity.Date:dd.MM.yyyy}.");
            notesChanged = true;
        }

        if (!previousIsPaid && entity.IsPaid)
        {
            var overdue = CalculateOverdueDays(entity.InitialDate, entity.PaidDate);
            var overdueInfo = overdue.HasValue && overdue.Value > 0 ? $" Просрочка {overdue.Value} дн." : string.Empty;
            AppendLine(sb, $"Оплата завершена {FormatDate(entity.PaidDate)}.{overdueInfo}");
            notesChanged = true;
        }

        if (!entity.IsPaid && previousDate.Date != entity.Date.Date)
        {
            entity.RescheduleCount = previousReschedules + 1;
            AppendLine(sb,
                $"Перенос платежа: {previousDate:dd.MM.yyyy} → {entity.Date:dd.MM.yyyy}. Остаток {FormatMoney(entity.OutstandingAmount)}.");
            notesChanged = true;
        }
        else
        {
            entity.RescheduleCount = previousReschedules;
        }

        if (!notesChanged)
        {
            entity.SystemNotes = previousNotes;
        }
        else
        {
            entity.SystemNotes = Truncate(sb.ToString().Trim());
        }
    }

    private static void MarkAsCompleted(Payment payment, DateTime nowUtc, DateTime? paidDate)
    {
        payment.PaidAmount = payment.Amount <= 0 ? 0 : payment.Amount;
        payment.IsPaid = true;
        payment.Status = PaymentStatus.Completed;
        payment.PaidDate = (paidDate ?? nowUtc).Date;
        payment.Date = payment.PaidDate.Value;
    }

    private static decimal NormalizePaidAmount(decimal value, decimal amount)
    {
        if (amount <= 0) return 0m;
        if (value <= 0) return 0m;
        var max = Math.Min(value, amount);
        return decimal.Round(max, 2, MidpointRounding.AwayFromZero);
    }

    private static string FormatMoney(decimal amount) => amount.ToString("N2", RuCulture);

    private static string FormatDate(DateTime? dt) => dt?.ToString("dd.MM.yyyy", RuCulture) ?? "?";

    private static void AppendLine(StringBuilder sb, string message)
    {
        var line = $"[{DateTime.UtcNow:dd.MM.yyyy HH:mm}] {message}";
        if (sb.Length == 0)
        {
            sb.Append(line);
        }
        else
        {
            sb.Insert(0, line + Environment.NewLine);
        }
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        const int limit = 3900;
        return text.Length <= limit ? text : text[..limit];
    }

    private static int? CalculateOverdueDays(DateTime? initialDate, DateTime? paidDate)
    {
        if (!initialDate.HasValue || !paidDate.HasValue) return null;
        var diff = (paidDate.Value.Date - initialDate.Value.Date).Days;
        return diff > 0 ? diff : 0;
    }
}
