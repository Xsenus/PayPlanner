using System.Globalization;
using System.Text.Json;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class PaymentRemainderTransferRules
{
    public const string PartialRemainderMarker = "[pp:partial-remainder]";
    public const string PartialRemainderSourceMarker = "[pp:partial-remainder-source]";
    public const string SourceDeleteBlockedMessage = "Платеж уже разделен частичной оплатой: остаток перенесен отдельным платежом. Удалять исходный платеж отдельно нельзя.";
    public const string SourceAmountUpdateBlockedMessage = "Платеж уже разделен частичной оплатой: сумма и оплаченная часть исходного платежа зафиксированы. Измените перенесенный остаток отдельной операцией.";
    public const string RemainderDeleteBlockedMessage = "Перенесенный остаток нельзя удалить отдельно: он связан с исходным частично оплаченным платежом.";
    public const string RemainderAmountUpdateBlockedMessage = "Сумма перенесенного остатка зафиксирована при частичной оплате. Можно оплатить остаток, но нельзя менять его сумму напрямую.";
    public const string RemainderCancelBlockedMessage = "Перенесенный остаток нельзя отменить напрямую: он связан с исходным частично оплаченным платежом.";
    public const string RemainderAlreadyProcessedMessage = "Операция недоступна: перенесенный остаток уже оплачен или по нему создан следующий перенос.";

    public static string BuildMarker(
        int sourcePaymentId,
        decimal sourceOriginalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate)
    {
        var payload = new
        {
            SourcePaymentId = sourcePaymentId,
            SourceOriginalAmount = NormalizeAmount(sourceOriginalAmount),
            PaidAmount = NormalizeAmount(paidAmount),
            RemainderAmount = NormalizeAmount(remainderAmount),
            RemainderDate = remainderDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        return $"{PartialRemainderMarker} {JsonSerializer.Serialize(payload)}";
    }

    public static string BuildSourceMarker(
        int sourcePaymentId,
        decimal sourceOriginalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate)
    {
        var payload = new
        {
            SourcePaymentId = sourcePaymentId,
            SourceOriginalAmount = NormalizeAmount(sourceOriginalAmount),
            PaidAmount = NormalizeAmount(paidAmount),
            RemainderAmount = NormalizeAmount(remainderAmount),
            RemainderDate = remainderDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        return $"{PartialRemainderSourceMarker} {JsonSerializer.Serialize(payload)}";
    }

    public static bool IsPartialRemainderPayment(Payment payment)
        => !string.IsNullOrWhiteSpace(payment.SystemNotes)
           && payment.SystemNotes.Contains(PartialRemainderMarker, StringComparison.Ordinal);

    public static bool IsPartialRemainderSourcePayment(Payment payment)
        => !string.IsNullOrWhiteSpace(payment.SystemNotes)
           && payment.SystemNotes.Contains(PartialRemainderSourceMarker, StringComparison.Ordinal);

    public static bool TryReadMarker(string? systemNotes, out PartialRemainderMarkerData data)
    {
        data = default;
        if (string.IsNullOrWhiteSpace(systemNotes))
        {
            return false;
        }

        var markerLine = systemNotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(PartialRemainderMarker, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(markerLine))
        {
            return false;
        }

        var json = markerLine[PartialRemainderMarker.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json) || json[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetInt32Property(root, "SourcePaymentId", out var sourcePaymentId)
                || !TryGetDecimalProperty(root, "SourceOriginalAmount", out var sourceOriginalAmount)
                || !TryGetDecimalProperty(root, "PaidAmount", out var paidAmount)
                || !TryGetDecimalProperty(root, "RemainderAmount", out var remainderAmount)
                || !TryGetDateProperty(root, "RemainderDate", out var remainderDate))
            {
                return false;
            }

            data = new PartialRemainderMarkerData(
                sourcePaymentId,
                NormalizeAmount(sourceOriginalAmount),
                NormalizeAmount(paidAmount),
                NormalizeAmount(remainderAmount),
                remainderDate.Date);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public readonly record struct PartialRemainderMarkerData(
        int SourcePaymentId,
        decimal SourceOriginalAmount,
        decimal PaidAmount,
        decimal RemainderAmount,
        DateTime RemainderDate);

    private static bool TryGetInt32Property(JsonElement element, string name, out int value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out value))
            {
                return true;
            }

            break;
        }

        value = 0;
        return false;
    }

    private static bool TryGetDecimalProperty(JsonElement element, string name, out decimal value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDecimal(out value))
            {
                return true;
            }

            break;
        }

        value = 0m;
        return false;
    }

    private static bool TryGetDateProperty(JsonElement element, string name, out DateTime value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && DateTime.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                return true;
            }

            break;
        }

        value = default;
        return false;
    }

    private static decimal NormalizeAmount(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
