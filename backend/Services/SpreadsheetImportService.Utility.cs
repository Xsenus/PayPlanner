using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private const string ImportMarker = "[pp:spreadsheet-import]";

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Replace('\u00A0', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeNameKey(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var character in normalized.ToLowerInvariant().Replace('ё', 'е'))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return string.Join(" ", builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> BuildCounterpartyMatchKeys(params string?[] values)
        => values
            .Select(NormalizeNameKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? NormalizeInn(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length switch
        {
            10 or 12 => digits,
            > 0 => digits,
            _ => null,
        };
    }

    private static string? NormalizePhone(string? value)
    {
        var digits = RussianPhoneNumberCanonicalizer.NormalizeDigits(value);
        return digits?.Length >= 6 ? digits : null;
    }

    private static string? NormalizeEmail(string? value)
    {
        var normalized = NormalizeText(value)?.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string BuildCounterpartyKey(string? name, string? inn, string? phone = null, string? email = null)
    {
        var normalizedInn = NormalizeInn(inn);
        if (!string.IsNullOrWhiteSpace(normalizedInn))
        {
            return $"inn:{normalizedInn}";
        }

        var normalizedName = NormalizeNameKey(name);
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            return $"name:{normalizedName}";
        }

        var normalizedPhone = NormalizePhone(phone);
        if (!string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return $"phone:{normalizedPhone}";
        }

        var normalizedEmail = NormalizeEmail(email);
        return string.IsNullOrWhiteSpace(normalizedEmail)
            ? string.Empty
            : $"email:{normalizedEmail}";
    }

    private static List<string> BuildCounterpartyKeys(string? name, string? inn, string? phone = null, string? email = null)
    {
        var keys = new List<string>();

        var normalizedInn = NormalizeInn(inn);
        if (!string.IsNullOrWhiteSpace(normalizedInn))
        {
            keys.Add($"inn:{normalizedInn}");
        }

        var normalizedName = NormalizeNameKey(name);
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            keys.Add($"name:{normalizedName}");
        }

        var normalizedPhone = NormalizePhone(phone);
        if (!string.IsNullOrWhiteSpace(normalizedPhone))
        {
            keys.Add($"phone:{normalizedPhone}");
        }

        var normalizedEmail = NormalizeEmail(email);
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            keys.Add($"email:{normalizedEmail}");
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string BuildInvoiceKey(string? number, DateTime? date)
        => BuildInvoiceKey(null, number, date, PaymentType.Income);

    private static string BuildInvoiceKey(string? number, DateTime? date, PaymentType type)
        => BuildInvoiceKey(null, number, date, type);

    private static string BuildInvoiceKey(string? externalId, string? number, DateTime? date, PaymentType type)
        => BuildInvoiceMatchKeys(externalId, number, date, type).FirstOrDefault() ?? string.Empty;

    private static List<string> BuildInvoiceMatchKeys(string? externalId, string? number, DateTime? date, PaymentType type)
        => BuildDocumentMatchKeys("invoice", externalId, number, date, type);

    private static string BuildActKey(string? number, DateTime? date)
        => BuildActKey(null, number, date, PaymentType.Income);

    private static string BuildActKey(string? number, DateTime? date, PaymentType type)
        => BuildActKey(null, number, date, type);

    private static string BuildActKey(string? externalId, string? number, DateTime? date, PaymentType type)
        => BuildActMatchKeys(externalId, number, date, type).FirstOrDefault() ?? string.Empty;

    private static List<string> BuildActMatchKeys(string? externalId, string? number, DateTime? date, PaymentType type)
        => BuildDocumentMatchKeys("act", externalId, number, date, type);

    private static List<string> BuildDocumentMatchKeys(string documentKind, string? externalId, string? number, DateTime? date, PaymentType type)
    {
        var keys = new List<string>();
        var normalizedNumber = NormalizeDocumentNumber(number);
        if (!string.IsNullOrWhiteSpace(normalizedNumber) && date.HasValue)
        {
            keys.Add($"{type}|{documentKind}|number:{normalizedNumber}|{date.Value:yyyy-MM-dd}");
        }

        var normalizedExternalId = NormalizeOneCExternalId(externalId);
        if (!string.IsNullOrWhiteSpace(normalizedExternalId))
        {
            keys.Add($"{type}|{documentKind}|uid:{normalizedExternalId}");
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string BuildInvoiceNaturalKey(string? number, DateTime? date, PaymentType type)
    {
        var normalizedNumber = NormalizeDocumentNumber(number);
        return string.IsNullOrWhiteSpace(normalizedNumber) || !date.HasValue
            ? string.Empty
            : $"{type}|invoice|number:{normalizedNumber}|{date.Value:yyyy-MM-dd}";
    }

    private static string BuildActNaturalKey(string? number, DateTime? date, PaymentType type)
    {
        var normalizedNumber = NormalizeDocumentNumber(number);
        return string.IsNullOrWhiteSpace(normalizedNumber) || !date.HasValue
            ? string.Empty
            : $"{type}|act|number:{normalizedNumber}|{date.Value:yyyy-MM-dd}";
    }

    private static string NormalizeDocumentNumber(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '/' or '-' or '_')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string? NormalizeOneCExternalId(string? value)
    {
        var normalized = NormalizeText(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.ToUpperInvariant();
    }

    internal static string? ExtractOneCExternalId(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        foreach (var marker in new[] { "UID 1С:", "UID 1C:" })
        {
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var start = markerIndex + marker.Length;
            var end = normalized.IndexOf(';', start);
            var raw = end >= 0
                ? normalized[start..end]
                : normalized[start..];
            return NormalizeOneCExternalId(raw);
        }

        return null;
    }

    private static LineItemParseResult ParseLineItems(string? rawValue, decimal totalAmount)
    {
        var value = NormalizeText(rawValue);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new LineItemParseResult(new List<ImportedLineItem>(), new List<string>());
        }

        var items = new List<ImportedLineItem>();
        var warnings = new List<string>();
        var lines = rawValue!
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        foreach (var line in lines)
        {
            var match = LineItemRegex.Match(line.Trim());
            if (!match.Success)
            {
                warnings.Add($"Не удалось разобрать строку номенклатуры: {line.Trim()}");
                continue;
            }

            var name = NormalizeText(match.Groups["name"].Value);
            var quantity = TryParseDecimal(match.Groups["quantity"].Value);
            var unit = NormalizeText(match.Groups["unit"].Value);
            var price = TryParseDecimal(match.Groups["price"].Value);

            if (string.IsNullOrWhiteSpace(name) || !quantity.HasValue || !price.HasValue)
            {
                warnings.Add($"Недостаточно данных в строке номенклатуры: {line.Trim()}");
                continue;
            }

            items.Add(new ImportedLineItem(
                name,
                quantity.Value <= 0 ? 1m : quantity.Value,
                unit ?? "шт",
                price.Value));
        }

        if (items.Count == 0 && totalAmount > 0)
        {
            items.Add(new ImportedLineItem(value, 1m, "шт", totalAmount));
            warnings.Add("Номенклатура импортирована одной агрегированной строкой.");
            return new LineItemParseResult(items, warnings);
        }

        var parsedTotal = items.Sum(item => Math.Round(item.Quantity * item.Price, 2));
        if (items.Count > 0 && totalAmount > 0 && Math.Abs(parsedTotal - totalAmount) > 0.01m)
        {
            warnings.Add($"Сумма строк ({parsedTotal:0.00}) не совпадает с суммой документа ({totalAmount:0.00}).");
        }

        return new LineItemParseResult(items, warnings);
    }

    private static List<ImportedLineItem> BuildEffectiveLineItems(
        IReadOnlyCollection<ImportedLineItem> parsedLineItems,
        decimal amount,
        string? fallbackDescription,
        string documentLabel)
    {
        if (parsedLineItems.Count > 0)
        {
            return parsedLineItems.ToList();
        }

        var description = NormalizeText(fallbackDescription) ?? documentLabel;
        var price = amount > 0 ? amount : 0m;
        return new List<ImportedLineItem>
        {
            new(description, 1m, "шт", price),
        };
    }

    private static string? ExtractNumberReference(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var markerIndex = normalized.IndexOf('№');
        if (markerIndex >= 0 && markerIndex + 1 < normalized.Length)
        {
            return NormalizeText(normalized[(markerIndex + 1)..]);
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? null : tokens[^1];
    }

    private static PaymentStatus MapPaymentStatus(string? statusText, DateTime? dueDate, DateTime? paidDate, DateTime localToday)
    {
        var normalized = NormalizeNameKey(statusText);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            if (normalized.Contains("отмен", StringComparison.Ordinal) || normalized.Contains("cancel", StringComparison.Ordinal))
            {
                return PaymentStatus.Cancelled;
            }

            if (normalized.Contains("частич", StringComparison.Ordinal) || normalized.Contains("partial", StringComparison.Ordinal))
            {
                return PaymentStatus.Processing;
            }

            if (normalized.Contains("оплачен", StringComparison.Ordinal) || normalized.Contains("paid", StringComparison.Ordinal))
            {
                return PaymentStatus.Completed;
            }

            if (normalized.Contains("обработ", StringComparison.Ordinal) || normalized.Contains("processing", StringComparison.Ordinal))
            {
                return PaymentStatus.Processing;
            }

            if (normalized.Contains("просроч", StringComparison.Ordinal) || normalized.Contains("overdue", StringComparison.Ordinal))
            {
                return PaymentStatus.Overdue;
            }
        }

        if (paidDate.HasValue)
        {
            return PaymentStatus.Completed;
        }

        return dueDate.HasValue && dueDate.Value.Date < localToday.Date
            ? PaymentStatus.Overdue
            : PaymentStatus.Pending;
    }

    private static ActStatus MapActStatus(string? statusText)
    {
        var normalized = NormalizeNameKey(statusText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ActStatus.Created;
        }

        if (normalized.Contains("подпис", StringComparison.Ordinal) || normalized.Contains("signed", StringComparison.Ordinal))
        {
            return ActStatus.Signed;
        }

        if (normalized.Contains("перед", StringComparison.Ordinal) || normalized.Contains("transfer", StringComparison.Ordinal))
        {
            return ActStatus.Transferred;
        }

        if (normalized.Contains("расторг", StringComparison.Ordinal)
            || normalized.Contains("аннули", StringComparison.Ordinal)
            || normalized.Contains("terminat", StringComparison.Ordinal))
        {
            return ActStatus.Terminated;
        }

        return ActStatus.Created;
    }

    private static decimal CalculatePaidAmount(PaymentStatus status, decimal amount, DateTime? paidDate, string? statusText)
    {
        var normalized = NormalizeNameKey(statusText);
        if (status == PaymentStatus.Completed || paidDate.HasValue)
        {
            return amount;
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && normalized.Contains("частич", StringComparison.Ordinal))
        {
            return 0m;
        }

        return 0m;
    }

    private static string BuildInvoiceDescription(InvoiceDraft draft)
    {
        var number = NormalizeText(draft.Number) ?? "без номера";
        var date = draft.Date.HasValue ? draft.Date.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "без даты";
        var counterparty = NormalizeText(draft.CounterpartyName);
        var description = $"Счет №{number} от {date}";
        return counterparty is null ? description : $"{description} · {counterparty}";
    }

    private static string SerializeSnapshot<T>(T snapshot)
        => JsonSerializer.Serialize(snapshot, JsonOptions);

    private static T? DeserializeSnapshot<T>(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static ClientSnapshot CaptureClientSnapshot(Client client)
        => new()
        {
            Id = client.Id,
            Name = client.Name,
            Email = client.Email,
            Phone = client.Phone,
            Company = client.Company,
            Address = client.Address,
            Notes = client.Notes,
            IsActive = client.IsActive,
            LegalEntityId = client.LegalEntityId,
            ResponsibleUserId = client.ResponsibleUserId,
            ClientStatusId = client.ClientStatusId,
        };

    private static LegalEntitySnapshot CaptureLegalEntitySnapshot(LegalEntity entity)
        => new()
        {
            Id = entity.Id,
            ShortName = entity.ShortName,
            FullName = entity.FullName,
            Inn = entity.Inn,
            Kpp = entity.Kpp,
            Ogrn = entity.Ogrn,
            Address = entity.Address,
            Phone = entity.Phone,
            Email = entity.Email,
            Director = entity.Director,
            ResponsibleUserId = entity.ResponsibleUserId,
            OrganizationForm = entity.OrganizationForm,
            BankAccountsJson = entity.BankAccountsJson,
            Notes = entity.Notes,
        };

    private static CounterpartySnapshot CaptureCounterpartySnapshot(Client? client, LegalEntity? legalEntity)
        => new()
        {
            Client = client is null ? null : CaptureClientSnapshot(client),
            LegalEntity = legalEntity is null ? null : CaptureLegalEntitySnapshot(legalEntity),
        };

    private static InvoiceSnapshot CaptureInvoiceSnapshot(Payment payment)
        => new()
        {
            Payment = new PaymentSnapshot
            {
                Id = payment.Id,
                InvoiceId = payment.InvoiceId,
                Account = payment.Account,
                AccountDate = payment.AccountDate,
                Amount = payment.Amount,
                Date = payment.Date,
                PlannedDate = payment.PlannedDate,
                Description = payment.Description,
                Status = payment.Status,
                Type = payment.Type,
                IsPaid = payment.IsPaid,
                PaidAmount = payment.PaidAmount,
                PaidDate = payment.PaidDate,
                LastPaymentDate = payment.LastPaymentDate,
                Notes = payment.Notes,
                SystemNotes = payment.SystemNotes,
                ClientId = payment.ClientId,
                ExpenseClientId = payment.ExpenseClientId,
                ContractId = payment.ContractId,
                DocumentKind = payment.DocumentKind,
                PaymentSourceId = payment.PaymentSourceId,
                ServiceId = payment.ServiceId,
                IncomeTypeId = payment.IncomeTypeId,
                DealTypeId = payment.DealTypeId,
                PaymentStatusId = payment.PaymentStatusId,
                IsNotForAccounting = payment.IsNotForAccounting,
            },
            InvoiceEntity = payment.Invoice is null
                ? null
                : new InvoiceEntitySnapshot
                {
                    Id = payment.Invoice.Id,
                    Number = payment.Invoice.Number,
                    Date = payment.Invoice.Date,
                },
            LineItems = payment.InvoiceLineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(item => new InvoiceLineItemSnapshot
                {
                    Id = item.Id,
                    SortOrder = item.SortOrder,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    Price = item.Price,
                })
                .ToList(),
            ResponsibleUserIds = payment.Responsibles
                .OrderBy(item => item.UserId)
                .Select(item => item.UserId)
                .ToList(),
        };

    private static ActSnapshot CaptureActSnapshot(Act act)
        => new()
        {
            Act = new ActEntitySnapshot
            {
                Id = act.Id,
                Number = act.Number,
                Title = act.Title,
                Date = act.Date,
                Amount = act.Amount,
                CreationMode = act.CreationMode,
                InvoiceNumber = act.InvoiceNumber,
                InvoiceId = act.InvoiceId,
                CounterpartyInn = act.CounterpartyInn,
                Status = act.Status,
                ClientId = act.ClientId,
                ResponsibleId = act.ResponsibleId,
                Comment = act.Comment,
            },
            LineItems = act.LineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(item => new ActLineItemSnapshot
                {
                    Id = item.Id,
                    SortOrder = item.SortOrder,
                    SourceKey = item.SourceKey,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    Price = item.Price,
                })
                .ToList(),
        };

    private static string? ExtractClientInn(string? notes)
    {
        var profile = ParseClientProfile(notes);
        return profile?["inn"]?.GetValue<string?>() is { } inn
            ? NormalizeInn(inn)
            : null;
    }

    private static JsonObject? ParseClientProfile(string? notes)
    {
        var payload = ExtractClientProfilePayload(notes);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractClientProfilePayload(string? notes)
    {
        var normalized = NormalizeText(notes);
        if (string.IsNullOrWhiteSpace(normalized)
            || !normalized.StartsWith(ClientProfileNotesPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return normalized[ClientProfileNotesPrefix.Length..].Trim();
    }

    private static string MergeClientNotes(string? existingNotes, CounterpartyDraft draft)
    {
        var profile = ParseClientProfile(existingNotes) ?? new JsonObject();
        var legacyNotes = string.IsNullOrWhiteSpace(existingNotes) || existingNotes.TrimStart().StartsWith(ClientProfileNotesPrefix, StringComparison.Ordinal)
            ? profile["legacyNotes"]?.GetValue<string?>()
            : existingNotes;

        SetProfileString(profile, "inn", NormalizeInn(draft.Inn));
        MergeProfileArray(profile, "phones", NormalizePhone(draft.Phone));
        MergeProfileArray(profile, "emails", NormalizeEmail(draft.Email));

        var commentParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(draft.ContactName))
        {
            commentParts.Add($"Контакт: {draft.ContactName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.ContractName))
        {
            commentParts.Add($"Договор: {draft.ContractName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.Address))
        {
            commentParts.Add($"Адрес: {draft.Address.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.Article))
        {
            commentParts.Add($"Статья: {draft.Article.Trim()}");
        }

        var mergedComment = MergeTextValues(profile["comment"]?.GetValue<string?>(), string.Join("; ", commentParts));
        SetProfileString(profile, "comment", mergedComment);
        SetProfileString(profile, "legacyNotes", legacyNotes);

        return $"{ClientProfileNotesPrefix}{profile.ToJsonString(JsonOptions)}";
    }

    private static void SetProfileString(JsonObject profile, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        profile[key] = Truncate(value, 1000);
    }

    private static void MergeProfileArray(JsonObject profile, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var array = profile[key] as JsonArray ?? new JsonArray();
        var existing = array
            .Select(node => node?.GetValue<string?>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(value))
        {
            array.Add(value);
        }

        profile[key] = array;
    }

    private static string MergeTextValues(string? current, string? additional)
    {
        var values = new[]
            {
                NormalizeText(current),
                NormalizeText(additional),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return Truncate(string.Join("; ", values), 1000);
    }

    private static string Truncate(string? value, int maxLength)
    {
        var normalized = NormalizeText(value) ?? string.Empty;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static bool IsLikelyOrganizationName(string? value)
    {
        var normalized = NormalizeNameKey(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return OrganizationMarkers.Any(marker => normalized.Contains(NormalizeNameKey(marker), StringComparison.Ordinal));
    }

    private static bool IsLikelyEntrepreneurName(string? value)
    {
        var normalized = NormalizeNameKey(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.StartsWith("ип", StringComparison.Ordinal)
            || compact.StartsWith("индивидуальныйпредприниматель", StringComparison.Ordinal);
    }

    private static string? ExtractEntrepreneurPersonalName(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var candidate = normalized;
        if (candidate.StartsWith("ИП ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[3..].Trim();
        }
        else if (candidate.StartsWith("Индивидуальный предприниматель ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["Индивидуальный предприниматель ".Length..].Trim();
        }

        return NormalizeText(candidate);
    }

    private static string NormalizeCounterpartyImportMode(string? value)
        => string.Equals(value, CounterpartyImportModeLegalEntityWithClient, StringComparison.OrdinalIgnoreCase)
            ? CounterpartyImportModeLegalEntityWithClient
            : CounterpartyImportModeClientOnly;

    private static List<string> GetAvailableCounterpartyImportModes()
        => new()
        {
            CounterpartyImportModeClientOnly,
            CounterpartyImportModeLegalEntityWithClient,
        };

    private static string ResolveDefaultCounterpartyImportMode(
        CounterpartyDraft draft,
        ClientStateItem? existingClient,
        LegalEntityStateItem? existingLegalEntity)
    {
        if (existingClient?.LegalEntityId.HasValue == true
            || existingLegalEntity is not null
            || IsLikelyEntrepreneurName(draft.Name)
            || IsLikelyOrganizationName(draft.Name)
            || HasLegalEntityDetails(draft))
        {
            return CounterpartyImportModeLegalEntityWithClient;
        }

        return CounterpartyImportModeClientOnly;
    }

    private static string? BuildCounterpartyImportModeHint(
        CounterpartyDraft draft,
        string importMode,
        ClientStateItem? existingClient,
        LegalEntityStateItem? existingLegalEntity)
    {
        if (IsLikelyEntrepreneurName(draft.Name))
        {
            return importMode == CounterpartyImportModeLegalEntityWithClient
                ? "Контрагент распознан как ИП. Будет создано или обновлено юридическое лицо и связанный клиент."
                : "Контрагент распознан как ИП. В этом режиме будет создан только клиент без отдельного юридического лица.";
        }

        if (existingClient?.LegalEntityId.HasValue == true || existingLegalEntity is not null)
        {
            return "Для контрагента уже найдено связанное юридическое лицо. Импорт сохранит или обновит эту связь.";
        }

        if (IsLikelyOrganizationName(draft.Name) || HasLegalEntityDetails(draft))
        {
            return importMode == CounterpartyImportModeLegalEntityWithClient
                ? "В выгрузке есть реквизиты организации. Будет создано или обновлено юридическое лицо и связанный клиент."
                : "В этом режиме реквизиты организации сохранятся только в карточке клиента без отдельного юридического лица.";
        }

        return "При необходимости контрагента можно импортировать не только как клиента, но и как связанное юридическое лицо.";
    }

    private static string BuildCounterpartyClientName(CounterpartyDraft draft, string importMode)
    {
        if (importMode == CounterpartyImportModeClientOnly && IsLikelyEntrepreneurName(draft.Name))
        {
            return ExtractEntrepreneurPersonalName(draft.Name) ?? draft.Name;
        }

        return draft.Name;
    }

    private static string BuildCounterpartyClientCompany(CounterpartyDraft draft, string importMode)
    {
        if (importMode == CounterpartyImportModeLegalEntityWithClient)
        {
            return draft.Name;
        }

        if (IsLikelyEntrepreneurName(draft.Name))
        {
            return draft.Name;
        }

        return IsLikelyOrganizationName(draft.Name) ? draft.Name : string.Empty;
    }

    private static string? BuildCounterpartyLegalEntityDirector(CounterpartyDraft draft)
        => NormalizeText(draft.ContactName) ?? ExtractEntrepreneurPersonalName(draft.Name);

    private static string? BuildCounterpartyOrganizationForm(CounterpartyDraft draft)
        => NormalizeText(draft.OrganizationForm)
            ?? (IsLikelyEntrepreneurName(draft.Name)
            ? "ИП"
            : null);

    private static string MergeLegalEntityNotes(string? existingNotes, CounterpartyDraft draft)
    {
        var commentParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(draft.ContactName))
        {
            commentParts.Add($"Контакт: {draft.ContactName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.OrganizationFormFull))
        {
            commentParts.Add($"Форма: {draft.OrganizationFormFull.Trim()}");
        }

        return MergeTextValues(existingNotes, string.Join("; ", commentParts));
    }

    private static bool HasLegalEntityDetails(CounterpartyDraft draft)
        => !string.IsNullOrWhiteSpace(draft.Kpp)
            || !string.IsNullOrWhiteSpace(draft.Ogrn)
            || !string.IsNullOrWhiteSpace(draft.Address)
            || !string.IsNullOrWhiteSpace(draft.OrganizationForm)
            || !string.IsNullOrWhiteSpace(draft.OrganizationFormFull)
            || !string.IsNullOrWhiteSpace(draft.BankAccount)
            || !string.IsNullOrWhiteSpace(draft.BankBik);

    private static string? MergeLegalEntityBankAccounts(string? existingJson, CounterpartyDraft draft)
    {
        var accountNumber = NormalizeText(draft.BankAccount);
        var bankName = NormalizeText(draft.BankName);
        var bik = NormalizeText(draft.BankBik);
        var corrAccount = NormalizeText(draft.BankCorrAccount);

        if (string.IsNullOrWhiteSpace(accountNumber)
            && string.IsNullOrWhiteSpace(bankName)
            && string.IsNullOrWhiteSpace(bik))
        {
            return existingJson;
        }

        JsonArray array;
        try
        {
            array = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonArray()
                : JsonNode.Parse(existingJson) as JsonArray ?? new JsonArray();
        }
        catch
        {
            array = new JsonArray();
        }

        var hasSameAccount = array
            .OfType<JsonObject>()
            .Any(item => string.Equals(
                NormalizeText(item["accountNumber"]?.GetValue<string?>()),
                accountNumber,
                StringComparison.OrdinalIgnoreCase));

        if (!hasSameAccount)
        {
            var item = new JsonObject();
            if (!string.IsNullOrWhiteSpace(bankName))
            {
                item["bank"] = bankName;
                item["name"] = bankName;
            }

            if (!string.IsNullOrWhiteSpace(bik))
            {
                item["bik"] = bik;
            }

            if (!string.IsNullOrWhiteSpace(accountNumber))
            {
                item["accountNumber"] = accountNumber;
            }

            if (!string.IsNullOrWhiteSpace(corrAccount))
            {
                item["corrAccountNumber"] = corrAccount;
            }

            item["accountType"] = "Расчетный";
            array.Add(item);
        }

        var json = array.ToJsonString(JsonOptions);
        return json.Length <= 16000 ? json : json[..16000];
    }

    private static string? ExtractBankAccountPreview(string? bankAccountsJson)
    {
        if (string.IsNullOrWhiteSpace(bankAccountsJson))
        {
            return null;
        }

        try
        {
            var array = JsonNode.Parse(bankAccountsJson) as JsonArray;
            var first = array?.OfType<JsonObject>().FirstOrDefault();
            return NormalizeText(first?["accountNumber"]?.GetValue<string?>());
        }
        catch
        {
            return null;
        }
    }

    private static string FormatPaymentTypeLabel(PaymentType type)
        => type == PaymentType.Expense ? "Расход" : "Доход";

    private static string? BuildInvoiceImportNotes(InvoiceDraft draft)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(draft.ExternalId))
        {
            parts.Add($"UID 1С: {draft.ExternalId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.ContractName))
        {
            parts.Add($"Договор 1С: {draft.ContractName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.Comment))
        {
            parts.Add($"Комментарий 1С: {draft.Comment.Trim()}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string? BuildActImportComment(ActDraft draft)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(draft.ExternalId))
        {
            parts.Add($"UID 1С: {draft.ExternalId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.ContractName))
        {
            parts.Add($"Договор 1С: {draft.ContractName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(draft.Comment))
        {
            parts.Add($"Комментарий 1С: {draft.Comment.Trim()}");
        }

        parts.AddRange(draft.ParseWarnings);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static CounterpartyDraft MergeCounterpartyDraft(CounterpartyDraft current, CounterpartyDraft incoming)
    {
        return current with
        {
            Inn = current.Inn ?? incoming.Inn,
            Kpp = current.Kpp ?? incoming.Kpp,
            Ogrn = current.Ogrn ?? incoming.Ogrn,
            Address = current.Address ?? incoming.Address,
            ContactName = current.ContactName ?? incoming.ContactName,
            Phone = current.Phone ?? incoming.Phone,
            Email = current.Email ?? incoming.Email,
            ContractName = current.ContractName ?? incoming.ContractName,
            DelayDays = current.DelayDays ?? incoming.DelayDays,
            Article = current.Article ?? incoming.Article,
            ResponsibleName = current.ResponsibleName ?? incoming.ResponsibleName,
            AutoInvoiceResponsibleName = current.AutoInvoiceResponsibleName ?? incoming.AutoInvoiceResponsibleName,
            ExternalId = current.ExternalId ?? incoming.ExternalId,
            OrganizationForm = current.OrganizationForm ?? incoming.OrganizationForm,
            OrganizationFormFull = current.OrganizationFormFull ?? incoming.OrganizationFormFull,
            BankName = current.BankName ?? incoming.BankName,
            BankBik = current.BankBik ?? incoming.BankBik,
            BankAccount = current.BankAccount ?? incoming.BankAccount,
            BankCorrAccount = current.BankCorrAccount ?? incoming.BankCorrAccount,
            Derived = current.Derived && incoming.Derived,
            DependencyPreviewIds = current.DependencyPreviewIds
                .Concat(incoming.DependencyPreviewIds)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            DependencyTitles = current.DependencyTitles
                .Concat(incoming.DependencyTitles)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static string MergeImportMarker(string? systemNotes, string sectionKey, string sourceFileName, int rowNumber)
    {
        var lines = (systemNotes ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(ImportMarker, StringComparison.Ordinal))
            .ToList();

        lines.Add($"{ImportMarker} section={sectionKey}; file={Path.GetFileName(sourceFileName)}; row={rowNumber}");
        return string.Join("\n", lines);
    }
}
