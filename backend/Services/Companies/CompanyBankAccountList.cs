using System.Text.Json;
using System.Text.Json.Nodes;

namespace PayPlanner.Api.Services.Companies;

/// <summary>
/// Приводит список банковских счетов компании (<c>LegalEntity.BankAccountsJson</c>) к единому виду:
/// у каждого счёта есть стабильный <c>id</c>, архивные счета не могут быть основными,
/// среди активных счетов основной ровно один. Незнакомые поля сохраняются без изменений.
/// Форматы и контрольные разряды (БИК, номер счёта) дают только предупреждения: в базе много
/// импортированных и тестовых счетов, которые нельзя отвергать при каждом сохранении компании.
/// </summary>
public static class CompanyBankAccountList
{
    public const int MaxJsonLength = 16000;
    private const string LegacyPrimaryType = "Основной";

    public sealed record Result(string? Json, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool IsValid => Errors.Count == 0;
    }

    /// <param name="previousJson">
    /// Ранее сохранённый список. Дубли, которые уже были в нём (старые и импортированные данные), остаются
    /// предупреждением и не мешают сохранить карточку; ошибкой становится только вновь появившийся дубль.
    /// </param>
    public static Result Normalize(string? rawJson, Func<string>? idFactory = null, string? previousJson = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new Result(null, [], []);
        }

        idFactory ??= static () => Guid.NewGuid().ToString("N");
        var errors = new List<string>();
        var warnings = new List<string>();

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(rawJson) as JsonArray;
        }
        catch (JsonException)
        {
            array = null;
        }

        if (array is null)
        {
            return new Result(rawJson, ["Список банковских счетов должен быть массивом."], warnings);
        }

        var items = new List<JsonObject>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
            {
                return new Result(rawJson, [$"Счёт №{i + 1}: ожидается объект."], warnings);
            }

            items.Add(item);
        }

        if (items.Count == 0)
        {
            return new Result(null, [], []);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = ReadString(item, "id");
            if (id is null || !seenIds.Add(id))
            {
                id = idFactory();
                seenIds.Add(id);
            }

            item["id"] = id;
        }

        var explicitPrimary = items.Any(item => ReadBool(item, "isPrimary") == true);
        var primaryAssigned = false;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var archived = ReadBool(item, "isArchived") == true;
            item["isArchived"] = archived;

            var wantsPrimary = explicitPrimary
                ? ReadBool(item, "isPrimary") == true
                : string.Equals(ReadString(item, "accountType"), LegacyPrimaryType, StringComparison.OrdinalIgnoreCase);

            var isPrimary = !archived && wantsPrimary && !primaryAssigned;
            primaryAssigned |= isPrimary;
            item["isPrimary"] = isPrimary;
        }

        if (!primaryAssigned)
        {
            var firstActive = items.FirstOrDefault(item => ReadBool(item, "isArchived") != true);
            if (firstActive is not null)
            {
                firstActive["isPrimary"] = true;
            }
        }

        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        var previousDuplicates = FindActiveDuplicateKeys(previousJson);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var number = ReadString(item, "accountNumber");
            var bik = ReadString(item, "bik");
            var label = $"Счёт №{i + 1}";

            if (number is null)
            {
                continue;
            }

            if (ReadBool(item, "isArchived") != true && !activeKeys.Add($"{bik}|{number}"))
            {
                var message = $"{label}: счёт {number} с таким БИК уже есть в списке.";
                if (previousDuplicates.Contains($"{bik}|{number}"))
                {
                    warnings.Add(message);
                }
                else
                {
                    errors.Add(message);
                }
            }

            if (bik is not null && !RussianRequisitesValidator.IsValidBik(bik))
            {
                warnings.Add($"{label}: БИК {bik} должен состоять из 9 цифр и начинаться с 04.");
            }

            if (!RussianRequisitesValidator.IsValidAccountFormat(number))
            {
                warnings.Add($"{label}: номер счёта должен состоять из 20 цифр.");
            }
            else if (bik is not null && RussianRequisitesValidator.IsValidBik(bik)
                     && !RussianRequisitesValidator.IsValidSettlementAccount(number, bik))
            {
                warnings.Add($"{label}: контрольный разряд счёта не сходится с БИК.");
            }

            var corr = ReadString(item, "corrAccountNumber");
            if (corr is not null && bik is not null && RussianRequisitesValidator.IsValidBik(bik)
                && !RussianRequisitesValidator.IsValidCorrespondentAccount(corr, bik))
            {
                warnings.Add($"{label}: корреспондентский счёт не сходится с БИК.");
            }
        }

        var json = new JsonArray(items.Select(item => (JsonNode?)item.DeepClone()).ToArray()).ToJsonString(
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (json.Length > MaxJsonLength)
        {
            errors.Add("Список банковских счетов слишком большой.");
        }

        return new Result(json, errors, warnings);
    }

    /// <summary>Основной активный счёт из нормализованного списка либо, для старых данных, из «сырого» JSON.</summary>
    public static JsonObject? FindPrimaryActive(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        var normalized = Normalize(rawJson, static () => string.Empty);
        if (normalized.Json is null || JsonNode.Parse(normalized.Json) is not JsonArray array)
        {
            return null;
        }

        return array.OfType<JsonObject>()
            .Where(item => ReadBool(item, "isArchived") != true && ReadString(item, "accountNumber") is not null)
            .OrderByDescending(item => ReadBool(item, "isPrimary") == true)
            .FirstOrDefault();
    }

    /// <summary>Идентификаторы действующих (не архивных) счетов нормализованного списка.</summary>
    public static HashSet<string> ActiveAccountIds(string? normalizedJson)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(normalizedJson))
        {
            return ids;
        }

        try
        {
            if (JsonNode.Parse(normalizedJson) is JsonArray array)
            {
                foreach (var item in array.OfType<JsonObject>())
                {
                    var id = ReadString(item, "id");
                    if (id is not null && ReadBool(item, "isArchived") != true)
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Некорректный список не содержит действующих счетов.
        }

        return ids;
    }

    private static HashSet<string> FindActiveDuplicateKeys(string? json)
    {
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return duplicates;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
            {
                return duplicates;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.OfType<JsonObject>())
            {
                var number = ReadString(item, "accountNumber");
                if (number is null || ReadBool(item, "isArchived") == true)
                {
                    continue;
                }

                var key = $"{ReadString(item, "bik")}|{number}";
                if (!seen.Add(key))
                {
                    duplicates.Add(key);
                }
            }
        }
        catch (JsonException)
        {
            // Некорректный прежний список просто не даёт исключений.
        }

        return duplicates;
    }

    private static string? ReadString(JsonObject item, string name)
    {
        if (!item.TryGetPropertyValue(name, out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool? ReadBool(JsonObject item, string name)
        => item.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : null;
}
