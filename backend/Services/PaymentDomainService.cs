using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>
/// Набор бизнес-правил для нормализации и автоматической корректировки платежей.
/// </summary>
public static class PaymentDomainService
{
    private const decimal AmountEpsilon = 0.01m;
    private const int MaxSystemNotesLength = 4000;

    /// <summary>
    /// Приводит модель платежа к согласованному виду: нормализует строки, даты и суммы.
    /// </summary>
    public static void Normalize(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        payment.Account = string.IsNullOrWhiteSpace(payment.Account) ? null : payment.Account.Trim();
        payment.Description ??= string.Empty;
        payment.Notes ??= string.Empty;
        payment.SystemNotes ??= string.Empty;
        payment.PaymentOrderNumber = string.IsNullOrWhiteSpace(payment.PaymentOrderNumber)
            ? null
            : payment.PaymentOrderNumber.Trim();

        payment.Amount = NormalizeAmount(payment.Amount);
        payment.PaidAmount = NormalizeAmount(payment.PaidAmount);
        if (payment.PaidAmount < 0m) payment.PaidAmount = 0m;
        if (payment.PaidAmount > payment.Amount) payment.PaidAmount = payment.Amount;

        if (payment.Type != PaymentType.Expense)
        {
            payment.ExpenseClientId = null;
            payment.ExpenseLegalEntityId = null;
        }

        payment.Date = NormalizeLocalDate(payment.Date);
        payment.PlannedDate = NormalizeLocalDate(payment.PlannedDate == default ? payment.Date : payment.PlannedDate);
        payment.AccountDate = NormalizeLocalDate(payment.AccountDate);
        payment.LastPaymentDate = NormalizeLocalDate(payment.LastPaymentDate);
        payment.PaidDate = NormalizeLocalDate(payment.PaidDate);
        payment.PaymentDocumentDate = NormalizeLocalDate(payment.PaymentDocumentDate);
    }

    /// <summary>
    /// Применяет автоматические правила установки статуса и вспомогательных полей.
    /// </summary>
    public static void ApplyStatusRules(Payment payment, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(payment);

        payment.Date = NormalizeLocalDate(payment.Date);
        payment.PlannedDate = NormalizeLocalDate(payment.PlannedDate == default ? payment.Date : payment.PlannedDate);
        payment.LastPaymentDate = NormalizeLocalDate(payment.LastPaymentDate);
        payment.PaidDate = NormalizeLocalDate(payment.PaidDate);

        var today = utcNow.Date;
        var fullyPaid = payment.PaidAmount >= payment.Amount - AmountEpsilon;

        if (fullyPaid)
        {
            payment.PaidAmount = payment.Amount;
            payment.IsPaid = true;
            payment.Status = PaymentStatus.Completed;
            payment.PaidDate = NormalizeLocalDate(payment.PaidDate ?? payment.LastPaymentDate ?? payment.Date);
            payment.LastPaymentDate = NormalizeLocalDate(payment.LastPaymentDate ?? payment.PaidDate);
            if (payment.LastPaymentDate.HasValue)
            {
                payment.Date = NormalizeLocalDate(payment.LastPaymentDate.Value);
            }
        }
        else
        {
            payment.IsPaid = false;
            payment.PaidDate = null;

            if (payment.Status is PaymentStatus.Cancelled or PaymentStatus.Processing)
            {
                // для ручных статусов оставляем текущее значение
                return;
            }

            payment.Status = payment.Date.Date < today
                ? PaymentStatus.Overdue
                : PaymentStatus.Pending;
        }
    }

    /// <summary>
    /// Добавляет строку в системные заметки, ограничивая их длину.
    /// </summary>
    public static void AppendSystemNote(
        Payment payment,
        string message,
        DateTime utcNow,
        string? userDisplayName = null,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (string.IsNullOrWhiteSpace(message)) return;

        var noteTimeZone = timeZone ?? TimeZoneInfo.Local;
        var formattedTimestamp = CalendarTimeZoneHelper.FormatUserDateTime(
            utcNow,
            noteTimeZone,
            "dd.MM.yyyy (HH:mm:ss)",
            CultureInfo.GetCultureInfo("ru-RU"));
        var author = string.IsNullOrWhiteSpace(userDisplayName)
            ? "Система"
            : userDisplayName!.Trim();
        var stamp = $"• {formattedTimestamp} — {author}: {message.Trim()}";
        var existing = string.IsNullOrWhiteSpace(payment.SystemNotes)
            ? new List<string>()
            : payment.SystemNotes
                .Split('\n')
                .Select(s => s.TrimEnd())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

        existing.Add(stamp);
        var combined = string.Join('\n', existing.TakeLast(200));
        if (combined.Length > MaxSystemNotesLength)
        {
            combined = combined[^MaxSystemNotesLength..];
            var idx = combined.IndexOf('\n');
            if (idx > 0 && idx < combined.Length - 1)
            {
                combined = combined[(idx + 1)..];
            }
        }

        payment.SystemNotes = combined;
    }

    private static decimal NormalizeAmount(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static DateTime NormalizeLocalDate(DateTime value)
        => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);

    private static DateTime? NormalizeLocalDate(DateTime? value)
        => value.HasValue ? NormalizeLocalDate(value.Value) : null;
}
