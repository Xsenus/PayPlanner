using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

/// <summary>Versioned logical input for the dedicated payment-create attempt protocol.</summary>
public static class PaymentCreateCommand
{
    public const string Version = "payment-create-v1";

    private enum FieldKind
    {
        String,
        EnumString,
        DateTimeString,
        Boolean,
        Decimal,
        Int32,
        NullableString,
        NullableEnumString,
        NullableDateTimeString,
        NullablePositiveInt32,
        PaymentKind,
    }

    private static readonly Dictionary<string, FieldKind> Fields = new(StringComparer.Ordinal)
    {
        ["date"] = FieldKind.DateTimeString,
        ["plannedDate"] = FieldKind.DateTimeString,
        ["amount"] = FieldKind.Decimal,
        ["paidAmount"] = FieldKind.Decimal,
        ["type"] = FieldKind.EnumString,
        ["status"] = FieldKind.EnumString,
        ["description"] = FieldKind.String,
        ["isPaid"] = FieldKind.Boolean,
        ["notes"] = FieldKind.String,
        ["systemNotes"] = FieldKind.String,
        ["rescheduleCount"] = FieldKind.Int32,
        ["paidDate"] = FieldKind.NullableDateTimeString,
        ["lastPaymentDate"] = FieldKind.NullableDateTimeString,
        ["advanceReviewStatus"] = FieldKind.EnumString,
        ["advanceReviewReason"] = FieldKind.NullableString,
        ["clientId"] = FieldKind.NullablePositiveInt32,
        ["legalEntityId"] = FieldKind.NullablePositiveInt32,
        ["expenseClientId"] = FieldKind.NullablePositiveInt32,
        ["expenseLegalEntityId"] = FieldKind.NullablePositiveInt32,
        ["contractId"] = FieldKind.NullablePositiveInt32,
        ["clientCaseId"] = FieldKind.NullablePositiveInt32,
        ["contractorUserId"] = FieldKind.NullablePositiveInt32,
        ["dealTypeId"] = FieldKind.NullablePositiveInt32,
        ["incomeTypeId"] = FieldKind.NullablePositiveInt32,
        ["serviceId"] = FieldKind.NullablePositiveInt32,
        ["paymentSourceId"] = FieldKind.NullablePositiveInt32,
        ["paymentStatusId"] = FieldKind.NullablePositiveInt32,
        ["invoiceId"] = FieldKind.NullablePositiveInt32,
        ["paymentOrderNumber"] = FieldKind.NullableString,
        ["paymentDocumentDate"] = FieldKind.NullableDateTimeString,
        ["account"] = FieldKind.NullableString,
        ["accountDate"] = FieldKind.NullableDateTimeString,
        ["overpaymentHandling"] = FieldKind.NullableEnumString,
        ["overpaymentTargetPaymentId"] = FieldKind.NullablePositiveInt32,
        ["overpaymentComment"] = FieldKind.NullableString,
        ["sellerCompanyId"] = FieldKind.NullablePositiveInt32,
        ["sellerBranchId"] = FieldKind.NullablePositiveInt32,
        ["documentKind"] = FieldKind.PaymentKind,
    };

    private static readonly string[] RequiredFields =
    [
        "date", "plannedDate", "amount", "paidAmount", "type", "status",
        "description", "isPaid", "notes", "systemNotes", "rescheduleCount",
    ];

    private readonly record struct Field(JsonElement Value, string? CanonicalNumber);

    public static bool TryRead(
        JsonElement value,
        JsonSerializerOptions options,
        out Payment? model,
        out string? identity,
        out string? error)
    {
        model = null;
        identity = null;
        error = null;

        if (options is null)
        {
            error = "Не заданы правила чтения команды платежа.";
            return false;
        }

        try
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                error = "Передайте объект с полями платежа.";
                return false;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fields = new SortedDictionary<string, Field>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    error = "Поля команды платежа не должны повторяться.";
                    return false;
                }

                if (!Fields.TryGetValue(property.Name, out var kind))
                {
                    error = "Команда содержит неизвестное поле или недопустимое имя поля.";
                    return false;
                }

                if (!TryReadField(property.Value, kind, out var number))
                {
                    error = $"Недопустимое значение поля {property.Name}.";
                    return false;
                }

                fields.Add(property.Name, new Field(property.Value, number));
            }

            foreach (var required in RequiredFields)
            {
                if (!fields.ContainsKey(required))
                {
                    error = $"Отсутствует обязательное поле {required}.";
                    return false;
                }
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var (name, field) in fields)
                {
                    writer.WritePropertyName(name);
                    if (field.CanonicalNumber is not null)
                    {
                        writer.WriteRawValue(field.CanonicalNumber);
                    }
                    else
                    {
                        switch (field.Value.ValueKind)
                        {
                            case JsonValueKind.String:
                                writer.WriteStringValue(field.Value.GetString());
                                break;
                            case JsonValueKind.True:
                                writer.WriteBooleanValue(true);
                                break;
                            case JsonValueKind.False:
                                writer.WriteBooleanValue(false);
                                break;
                            case JsonValueKind.Null:
                                writer.WriteNullValue();
                                break;
                            default:
                                throw new InvalidOperationException("A command field was not validated.");
                        }
                    }
                }
                writer.WriteEndObject();
            }

            var canonical = buffer.ToArray();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(Version + "\0"));
            hash.AppendData(canonical);
            var commandIdentity = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            // Defaults such as Payment.CreatedAt are applied only after the logical identity.
            // Bind canonical numbers so exact integral exponent/fraction spellings also reach
            // the same int32 value, without depending on a rounded decimal reader result.
            var parsed = JsonSerializer.Deserialize<Payment>(canonical, options);
            if (parsed is null
                || parsed.DocumentKind != DocumentKind.Payment
                || !Enum.IsDefined(parsed.Type)
                || !Enum.IsDefined(parsed.Status)
                || !Enum.IsDefined(parsed.AdvanceReviewStatus)
                || (parsed.OverpaymentHandling.HasValue && !Enum.IsDefined(parsed.OverpaymentHandling.Value)))
            {
                error = "Не удалось прочитать тип или статус платежа.";
                return false;
            }

            model = parsed;
            identity = commandIdentity;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException
            or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            error = "Не удалось прочитать команду платежа по действующим правилам.";
            return false;
        }
    }

    private static bool TryReadField(JsonElement value, FieldKind kind, out string? canonicalNumber)
    {
        canonicalNumber = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return kind is FieldKind.NullableString or FieldKind.NullableEnumString
                or FieldKind.NullableDateTimeString or FieldKind.NullablePositiveInt32;
        }

        switch (kind)
        {
            case FieldKind.String:
            case FieldKind.EnumString:
            case FieldKind.DateTimeString:
            case FieldKind.NullableString:
            case FieldKind.NullableEnumString:
            case FieldKind.NullableDateTimeString:
                return value.ValueKind == JsonValueKind.String;
            case FieldKind.PaymentKind:
                return value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), "Payment", StringComparison.Ordinal);
            case FieldKind.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case FieldKind.Decimal:
            case FieldKind.Int32:
            case FieldKind.NullablePositiveInt32:
                if (value.ValueKind != JsonValueKind.Number
                    || !TryCanonicalNumber(value.GetRawText(), out var number, out var exact))
                    return false;
                if (kind is FieldKind.Int32 or FieldKind.NullablePositiveInt32)
                {
                    if (exact < int.MinValue || exact > int.MaxValue || decimal.Truncate(exact) != exact)
                        return false;
                    if (kind == FieldKind.NullablePositiveInt32 && exact <= 0m)
                        return false;
                }
                canonicalNumber = number;
                return true;
            default:
                return false;
        }
    }

    private static bool TryCanonicalNumber(string raw, out string canonical, out decimal exact)
    {
        canonical = string.Empty;
        exact = 0m;
        var negative = raw[0] == '-';
        var start = negative ? 1 : 0;
        var exponentAt = raw.IndexOf('e');
        if (exponentAt < 0) exponentAt = raw.IndexOf('E');
        var end = exponentAt < 0 ? raw.Length : exponentAt;
        var point = raw.IndexOf('.', start, end - start);
        var fractionalDigits = point < 0 ? 0 : end - point - 1;
        var first = -1;
        var last = -1;
        var trailingZeros = 0;
        for (var index = start; index < end; index++)
        {
            if (raw[index] == '.') continue;
            if (raw[index] != '0')
            {
                if (first < 0) first = index;
                last = index;
                trailingZeros = 0;
            }
            else if (first >= 0)
            {
                trailingZeros++;
            }
        }

        // JsonElement has already validated the JSON number grammar. Zero remains
        // exactly zero even when its exponent is too large for any integer parser.
        if (first < 0)
        {
            canonical = "0";
            return true;
        }

        var significantDigits = last - first + 1 - (point >= first && point <= last ? 1 : 0);
        if (significantDigits > 29) return false;
        var coefficientText = raw.Substring(first, last - first + 1).Replace(".", string.Empty, StringComparison.Ordinal);
        if (!decimal.TryParse(coefficientText, NumberStyles.None, CultureInfo.InvariantCulture, out var coefficient))
            return false;

        long exponent = 0;
        if (exponentAt >= 0)
        {
            var index = exponentAt + 1;
            var exponentNegative = raw[index] == '-';
            if (raw[index] is '-' or '+') index++;
            // A JSON string's length is int-bounded. Beyond this magnitude no
            // nonzero significand can cancel to decimal's [-28, 28] range.
            const long saturation = (long)int.MaxValue + 100L;
            for (; index < raw.Length; index++)
                exponent = Math.Min(saturation, exponent * 10 + raw[index] - '0');
            if (exponentNegative) exponent = -exponent;
        }

        var normalizedExponent = exponent - fractionalDigits + trailingZeros;
        if (normalizedExponent < -28 || normalizedExponent > 28) return false;
        if (normalizedExponent >= 0)
        {
            for (var index = 0; index < normalizedExponent; index++)
            {
                if (coefficient > decimal.MaxValue / 10m) return false;
                coefficient *= 10m;
            }
            exact = negative ? -coefficient : coefficient;
            canonical = coefficientText + new string('0', (int)normalizedExponent);
        }
        else
        {
            var bits = decimal.GetBits(coefficient);
            exact = new decimal(bits[0], bits[1], bits[2], negative, (byte)-normalizedExponent);
            var position = coefficientText.Length + (int)normalizedExponent;
            canonical = position > 0
                ? coefficientText.Insert(position, ".")
                : "0." + new string('0', -position) + coefficientText;
        }

        if (negative) canonical = "-" + canonical;
        return true;
    }
}
