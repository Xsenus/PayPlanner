using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Documents;

public static class DocumentTemplateHistoryService
{
    private const string AuditObjectType = "document-template";
    private const string VersionCreatedAction = "TemplateVersionCreated";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string SerializeSettings(InvoiceDocumentTemplateSettingsDto settings)
        => JsonSerializer.Serialize(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = settings.ActiveTemplateKey,
            PrintWithSignatureAndSealByDefault = settings.PrintWithSignatureAndSealByDefault,
            Templates = settings.Templates.Select(template => new InvoiceDocumentTemplateRequest
            {
                Key = template.Key,
                Name = template.Name,
                Description = template.Description,
                Html = template.Html,
            }).ToList(),
        });

    public static InvoiceDocumentTemplateSettingsRequest? DeserializeSettings(string settingsJson)
        => JsonSerializer.Deserialize<InvoiceDocumentTemplateSettingsRequest>(settingsJson, JsonOptions);

    public static async Task<bool> SaveVersionedSettingAsync(
        PaymentContext db,
        string settingKey,
        int historyObjectId,
        string documentType,
        string defaultSettingsJson,
        string newSettingsJson,
        int? actorUserId,
        string? actorUserName,
        DateTime nowUtc,
        int? restoredFromVersionId,
        CancellationToken ct)
        => await SaveRawVersionedSettingAsync(
            db,
            settingKey,
            historyObjectId,
            documentType,
            defaultSettingsJson,
            newSettingsJson,
            ReadSettingsSummary,
            actorUserId,
            actorUserName,
            nowUtc,
            restoredFromVersionId,
            ct);

    public static async Task<bool> SaveRawVersionedSettingAsync(
        PaymentContext db,
        string settingKey,
        int historyObjectId,
        string documentType,
        string defaultSettingsJson,
        string newSettingsJson,
        Func<string, DocumentTemplateVersionSummary> readSummary,
        int? actorUserId,
        string? actorUserName,
        DateTime nowUtc,
        int? restoredFromVersionId,
        CancellationToken ct)
    {
        var setting = await db.SystemSettings.FirstOrDefaultAsync(item => item.Key == settingKey, ct);
        var currentSettingsJson = setting?.Value ?? defaultSettingsJson;
        var currentHash = ComputeHash(currentSettingsJson);
        var newHash = ComputeHash(newSettingsJson);

        if (string.Equals(currentHash, newHash, StringComparison.Ordinal))
        {
            return false;
        }

        var latestEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == AuditObjectType
                && item.ObjectId == historyObjectId
                && item.Action == VersionCreatedAction)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new { item.Id, item.MetadataJson })
            .FirstOrDefaultAsync(ct);

        var latestMetadata = DeserializeMetadata(latestEvent?.MetadataJson);
        var revision = latestMetadata?.Revision ?? 0;

        if (!string.Equals(latestMetadata?.ContentHash, currentHash, StringComparison.Ordinal))
        {
            revision += 1;
            AddVersionEvent(
                db,
                historyObjectId,
                documentType,
                revision,
                currentSettingsJson,
                currentHash,
                actorUserId,
                actorUserName,
                nowUtc,
                null,
                "Сохранено исходное состояние шаблонов перед изменением.",
                readSummary(currentSettingsJson));
        }

        revision += 1;
        AddVersionEvent(
            db,
            historyObjectId,
            documentType,
            revision,
            newSettingsJson,
            newHash,
            actorUserId,
            actorUserName,
            nowUtc,
            restoredFromVersionId,
            restoredFromVersionId.HasValue
                ? $"Восстановлена версия шаблонов #{restoredFromVersionId.Value}."
                : "Сохранена новая версия шаблонов.",
            readSummary(newSettingsJson));

        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = settingKey,
                Value = newSettingsJson,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
        }
        else
        {
            setting.Value = newSettingsJson;
            setting.UpdatedAt = nowUtc;
        }

        return true;
    }

    public static async Task<IReadOnlyList<DocumentTemplateVersionDto>> GetVersionsAsync(
        PaymentContext db,
        int historyObjectId,
        CancellationToken ct)
    {
        var events = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == AuditObjectType
                && item.ObjectId == historyObjectId
                && item.Action == VersionCreatedAction)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(100)
            .Select(item => new
            {
                item.Id,
                item.ActorUserId,
                item.ActorUserName,
                item.CreatedAt,
                item.MetadataJson,
            })
            .ToListAsync(ct);

        return events
            .Select(item => (Item: item, Metadata: DeserializeMetadata(item.MetadataJson)))
            .Where(item => item.Metadata is not null)
            .Select(item => new DocumentTemplateVersionDto
            {
                Id = item.Item.Id,
                Revision = item.Metadata!.Revision,
                DocumentType = item.Metadata.DocumentType,
                ActiveTemplateKey = item.Metadata.ActiveTemplateKey,
                TemplateNames = item.Metadata.TemplateNames,
                CreatedByUserId = item.Item.ActorUserId,
                CreatedByUserName = item.Item.ActorUserName,
                CreatedAt = item.Item.CreatedAt,
                RestoredFromVersionId = item.Metadata.RestoredFromVersionId,
            })
            .ToList();
    }

    public static async Task<string?> GetVersionSettingsAsync(
        PaymentContext db,
        int historyObjectId,
        int versionId,
        CancellationToken ct)
    {
        var metadataJson = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.Id == versionId
                && item.ObjectType == AuditObjectType
                && item.ObjectId == historyObjectId
                && item.Action == VersionCreatedAction)
            .Select(item => item.MetadataJson)
            .FirstOrDefaultAsync(ct);

        return DeserializeMetadata(metadataJson)?.SettingsJson;
    }

    private static void AddVersionEvent(
        PaymentContext db,
        int historyObjectId,
        string documentType,
        int revision,
        string settingsJson,
        string contentHash,
        int? actorUserId,
        string? actorUserName,
        DateTime createdAt,
        int? restoredFromVersionId,
        string description,
        DocumentTemplateVersionSummary summary)
    {
        var metadata = new TemplateVersionMetadata
        {
            Revision = revision,
            DocumentType = documentType,
            ActiveTemplateKey = summary.ActiveTemplateKey,
            TemplateNames = summary.TemplateNames.ToList(),
            ContentHash = contentHash,
            SettingsJson = settingsJson,
            RestoredFromVersionId = restoredFromVersionId,
        };

        db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = AuditObjectType,
            ObjectId = historyObjectId,
            Action = VersionCreatedAction,
            Description = description,
            ActorUserId = actorUserId,
            ActorUserName = actorUserName,
            MetadataJson = JsonSerializer.Serialize(metadata),
            CreatedAt = createdAt,
        });
    }

    private static DocumentTemplateVersionSummary ReadSettingsSummary(string settingsJson)
    {
        var request = DeserializeSettings(settingsJson);
        return request is null
            ? new DocumentTemplateVersionSummary(string.Empty, [])
            : new DocumentTemplateVersionSummary(
                request.ActiveTemplateKey,
                request.Templates.Select(template => template.Name).ToList());
    }

    public sealed record DocumentTemplateVersionSummary(
        string ActiveTemplateKey,
        IReadOnlyList<string> TemplateNames);

    private static TemplateVersionMetadata? DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TemplateVersionMetadata>(metadataJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class TemplateVersionMetadata
    {
        public int Revision { get; set; }
        public string DocumentType { get; set; } = string.Empty;
        public string ActiveTemplateKey { get; set; } = string.Empty;
        public List<string> TemplateNames { get; set; } = new();
        public string ContentHash { get; set; } = string.Empty;
        public string SettingsJson { get; set; } = string.Empty;
        public int? RestoredFromVersionId { get; set; }
    }
}
