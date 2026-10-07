using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Documents;

public readonly record struct CaseDocumentTemplateDefinition(
    string Key,
    string Title,
    string DocumentType,
    string Heading,
    IReadOnlyList<string>? CaseTypes = null,
    string? Description = null,
    string? Content = null,
    int Version = 1,
    bool IsActive = true,
    DateTime? UpdatedAt = null);

public sealed class CaseDocumentTemplateCatalog(PaymentContext db)
{
    public const string CaseDocumentTemplatesSettingKey = "cases.documentTemplates";
    internal static readonly JsonSerializerOptions CaseDocumentTemplateJsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly CaseDocumentTemplateDefinition[] CaseDocumentTemplateDefinitions =
    [
        new("case-summary", "Справка по делу", "Карточка дела", "Справка по делу"),
        new("claim", "Исковое заявление", "Иск", "Исковое заявление"),
        new("motion", "Ходатайство", "Ходатайство", "Ходатайство"),
        new("objection", "Возражение", "Возражение", "Возражение"),
        new("court-letter", "Письмо в суд", "Переписка", "Письмо в суд"),
        new("power-of-attorney", "Доверенность", "Доверенность", "Доверенность"),
        new("application", "Заявление", "Заявление", "Заявление"),
        new("procedural-document", "Процессуальный документ", "Процессуальный документ", "Процессуальный документ"),
    ];

    public async Task<IReadOnlyList<CaseDocumentTemplateDefinition>> ReadAsync(CancellationToken ct)
    {
        var rawJson = await db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == CaseDocumentTemplatesSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        var templates = DeserializeCaseDocumentTemplateDefinitions(rawJson);

        return NormalizeCaseDocumentTemplateLibrary(
            templates is { Count: > 0 }
                ? templates
                : CaseDocumentTemplateDefinitions.ToList());
    }

    public static (CaseDocumentTemplateDefinition Template, string? Error, bool NotFound) Resolve(
        IReadOnlyList<CaseDocumentTemplateDefinition> templates, string? templateKey, string? documentType)
    {
        var requestedTemplateKey = NormalizeOptional(templateKey);
        if (requestedTemplateKey is not null)
        {
            var normalizedKey = NormalizeDocumentTemplateCode(requestedTemplateKey);
            if (normalizedKey is null)
            {
                return (default, "Некорректный код шаблона.", false);
            }

            var template = templates.FirstOrDefault(item =>
                item.IsActive && string.Equals(item.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(template.Key))
            {
                return (template, null, false);
            }

            var legacyKey = NormalizeCaseDocumentTemplateKey(requestedTemplateKey, documentType);
            if (!string.Equals(legacyKey, normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                template = templates.FirstOrDefault(item =>
                    item.IsActive && string.Equals(item.Key, legacyKey, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(template.Key))
                {
                    return (template, null, false);
                }
            }

            return (default, "Активный шаблон документа не найден.", true);
        }

        var inferredKey = NormalizeCaseDocumentTemplateKey(null, documentType);
        var inferredTemplate = templates.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.Key, inferredKey, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(inferredTemplate.Key))
        {
            return (inferredTemplate, null, false);
        }

        return (CaseDocumentTemplateDefinitions[0], null, false);
    }

    public static string SerializeCaseDocumentTemplateLibrary(
        IEnumerable<CaseDocumentTemplateDefinition> templates,
        DateTime? defaultUpdatedAt)
    {
        var storage = new CaseDocumentTemplateLibraryStorage
        {
            Templates = NormalizeCaseDocumentTemplateLibrary(templates)
                .Select(template => new CaseDocumentTemplateStorage
                {
                    Key = template.Key,
                    Label = template.Title,
                    DocumentType = template.DocumentType,
                    Heading = template.Heading,
                    CaseTypes = NormalizeCaseDocumentTemplateCaseTypes(template.CaseTypes),
                    Description = template.Description,
                    Content = template.Content,
                    Version = template.Version <= 0 ? 1 : template.Version,
                    IsActive = template.IsActive,
                    UpdatedAt = template.UpdatedAt ?? defaultUpdatedAt,
                })
                .ToList(),
        };
        return JsonSerializer.Serialize(storage, CaseDocumentTemplateJsonOptions);
    }

    public static List<CaseDocumentTemplateDefinition>? DeserializeCaseDocumentTemplateDefinitions(string? rawJson)
    {
        var storage = DeserializeCaseDocumentTemplateLibrary(rawJson);
        return storage?.Templates
            .Select(static item => new CaseDocumentTemplateDefinition(
                item.Key,
                item.Label,
                item.DocumentType,
                item.Heading,
                NormalizeCaseDocumentTemplateCaseTypes(item.CaseTypes),
                item.Description,
                item.Content,
                item.Version <= 0 ? 1 : item.Version,
                item.IsActive,
                item.UpdatedAt))
            .ToList();
    }

    private static CaseDocumentTemplateLibraryStorage? DeserializeCaseDocumentTemplateLibrary(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CaseDocumentTemplateLibraryStorage>(rawJson, CaseDocumentTemplateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<CaseDocumentTemplateDefinition> NormalizeCaseDocumentTemplateLibrary(
        IEnumerable<CaseDocumentTemplateDefinition> templates)
    {
        var result = new List<CaseDocumentTemplateDefinition>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            var key = NormalizeDocumentTemplateCode(template.Key);
            if (key is null || !usedKeys.Add(key))
            {
                continue;
            }

            var title = NormalizeRequired(template.Title, template.Heading);
            title = NormalizeRequired(title, key);
            var documentType = NormalizeRequired(template.DocumentType, "Документ");
            result.Add(template with
            {
                Key = key,
                Title = title,
                DocumentType = documentType,
                Heading = NormalizeRequired(template.Heading, title),
                CaseTypes = NormalizeCaseDocumentTemplateCaseTypes(template.CaseTypes),
                Version = template.Version <= 0 ? 1 : template.Version,
                IsActive = template.IsActive,
            });
        }

        return result.Count > 0
            ? result
            : CaseDocumentTemplateDefinitions.ToList();
    }

    public static List<string> NormalizeCaseDocumentTemplateCaseTypes(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return new List<string>();
        }

        return values
            .SelectMany(static value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeOptional)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Create(new CultureInfo("ru-RU"), ignoreCase: true))
            .ToList();
    }

    public static bool IsCaseDocumentTemplateApplicableToCase(CaseDocumentTemplateDefinition template, ClientCase clientCase)
    {
        var caseTypes = NormalizeCaseDocumentTemplateCaseTypes(template.CaseTypes);
        if (caseTypes.Count == 0)
        {
            return true;
        }

        var caseType = NormalizeOptional(clientCase.CaseType);
        return caseType is not null && caseTypes.Contains(caseType, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeCaseDocumentTemplateKey(string? templateKey, string? documentType)
    {
        var normalized = templateKey?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized switch
            {
                "claim" or "isk" => "claim",
                "motion" => "motion",
                "objection" => "objection",
                "court-letter" or "letter" => "court-letter",
                _ => "case-summary",
            };
        }

        var type = documentType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type.Contains("иск", StringComparison.OrdinalIgnoreCase)) return "claim";
        if (type.Contains("ходатай", StringComparison.OrdinalIgnoreCase)) return "motion";
        if (type.Contains("возраж", StringComparison.OrdinalIgnoreCase)) return "objection";
        if (type.Contains("переписк", StringComparison.OrdinalIgnoreCase) || type.Contains("письм", StringComparison.OrdinalIgnoreCase)) return "court-letter";
        return "case-summary";
    }

    public static string? NormalizeDocumentTemplateCode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64)
        {
            return null;
        }

        return normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            ? normalized
            : null;
    }

    private static string NormalizeRequired(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed class CaseDocumentTemplateLibraryStorage
    {
        public List<CaseDocumentTemplateStorage> Templates { get; set; } = new();
    }

    private sealed class CaseDocumentTemplateStorage
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public List<string> CaseTypes { get; set; } = new();
        public string? Description { get; set; }
        public string? Content { get; set; }
        public int Version { get; set; } = 1;
        public bool IsActive { get; set; } = true;
        public DateTime? UpdatedAt { get; set; }
    }
}
