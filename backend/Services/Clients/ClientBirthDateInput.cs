using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientBirthDateInput
{
    private const string ProfilePrefix = "__PP_CLIENT_PROFILE_V2__:";

    public static IActionResult? Resolve(Client requested, DateTime? current, out DateTime? resolved)
    {
        // Missing dates never rewrite an existing stored value. A profile field
        // explicitly set to null/blank is the compatible way to clear the date.
        resolved = current;
        var raw = requested.Notes?.Trim();
        var specified = false;
        DateTime? profileDate = null;
        if (raw?.StartsWith(ProfilePrefix, StringComparison.Ordinal) == true)
        {
            try
            {
                using var document = JsonDocument.Parse(raw[ProfilePrefix.Length..].Trim());
                if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return Invalid();
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (!property.Name.Equals("birthDate", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!TryReadDate(property.Value, out var date)) return Invalid();
                        if (specified && date != profileDate) return Conflict();
                        specified = true;
                        profileDate = date;
                    }
                }
            }
            catch (JsonException) { return Invalid(); }
        }
        // Compare calendar days as supplied. Never convert a birthday to UTC.
        var suppliedDate = requested.BirthDate?.Date;
        if (specified && suppliedDate.HasValue && profileDate != suppliedDate) return Conflict();
        if (specified) resolved = profileDate;
        else if (suppliedDate.HasValue) resolved = suppliedDate;
        return null;
    }

    private static bool TryReadDate(JsonElement element, out DateTime? date)
    {
        date = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed.Date;
        return true;
    }

    private static IActionResult Invalid() => new BadRequestObjectResult(new
    {
        message = "Укажите корректную дату рождения и повторите сохранение профиля."
    });

    private static IActionResult Conflict() => new BadRequestObjectResult(new
    {
        message = "Переданы разные даты рождения. Обновите данные профиля и повторите сохранение."
    });
}
