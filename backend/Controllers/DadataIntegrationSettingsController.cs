using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/integrations/dadata/settings")]
[Authorize]
public sealed class DadataIntegrationSettingsController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly DadataOptions _options;

    public DadataIntegrationSettingsController(PaymentContext db, IOptions<DadataOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<ActionResult<DadataIntegrationSettingsDto>> Get(CancellationToken ct)
    {
        if (!await HasIntegrationsPermissionAsync(permission => permission.CanView, ct))
            return Forbid();

        var canEdit = await HasIntegrationsPermissionAsync(permission => permission.CanEdit, ct);
        var settings = await LoadSettingsAsync(ct);
        var settingValues = settings.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var legacyCredentials = await LoadLegacyAdminCredentialsAsync(ct);
        var apiKey = FirstNonWhiteSpace(
            settingValues.GetValueOrDefault(DadataLegalEntityEnrichmentService.ApiKeySettingKey),
            _options.ApiKey,
            legacyCredentials.ApiKey);
        var secret = FirstNonWhiteSpace(
            settingValues.GetValueOrDefault(DadataLegalEntityEnrichmentService.SecretSettingKey),
            _options.Secret,
            legacyCredentials.Secret);
        var updatedAt = settings
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => item.UpdatedAt)
            .DefaultIfEmpty()
            .Max();

        return Ok(new DadataIntegrationSettingsDto
        {
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(apiKey),
            SecretConfigured = !string.IsNullOrWhiteSpace(secret),
            ApiKeyMasked = MaskSecret(apiKey),
            SecretMasked = MaskSecret(secret),
            CanEdit = canEdit,
            UpdatedAt = updatedAt == default ? null : updatedAt,
        });
    }

    [HttpPut]
    public async Task<ActionResult<DadataIntegrationSettingsDto>> Update(
        [FromBody] UpdateDadataIntegrationSettingsRequest request,
        CancellationToken ct)
    {
        if (!await HasIntegrationsPermissionAsync(permission => permission.CanEdit, ct))
            return Forbid();

        var now = DateTime.UtcNow;
        if (request.ApiKey is not null)
        {
            await UpsertSettingAsync(
                DadataLegalEntityEnrichmentService.ApiKeySettingKey,
                request.ApiKey.Trim(),
                now,
                ct);
        }

        if (request.Secret is not null)
        {
            await UpsertSettingAsync(
                DadataLegalEntityEnrichmentService.SecretSettingKey,
                request.Secret.Trim(),
                now,
                ct);
        }

        await _db.SaveChangesAsync(ct);
        return await Get(ct);
    }

    private Task<bool> HasIntegrationsPermissionAsync(Func<RolePermissionValues, bool> isAllowed, CancellationToken ct)
        => _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Integrations, isAllowed, ct);

    private async Task<List<SystemSetting>> LoadSettingsAsync(CancellationToken ct)
    {
        var keys = new[]
        {
            DadataLegalEntityEnrichmentService.ApiKeySettingKey,
            DadataLegalEntityEnrichmentService.SecretSettingKey,
        };

        return await _db.SystemSettings
            .AsNoTracking()
            .Where(item => keys.Contains(item.Key))
            .ToListAsync(ct);
    }

    private async Task<(string? ApiKey, string? Secret)> LoadLegacyAdminCredentialsAsync(CancellationToken ct)
    {
        var keys = new[]
        {
            DadataLegalEntityEnrichmentService.ApiKeySettingKey,
            DadataLegalEntityEnrichmentService.SecretSettingKey,
        };
        var settings = await _db.UserSettings
            .AsNoTracking()
            .Where(item =>
                keys.Contains(item.Key)
                && item.User != null
                && item.User.IsActive
                && item.User.Role != null
                && item.User.Role.Name == "admin"
                && !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => new
            {
                item.UserId,
                item.Key,
                item.Value,
                item.UpdatedAt,
            })
            .ToListAsync(ct);

        var group = settings
            .GroupBy(item => item.UserId)
            .OrderByDescending(group => group.Max(item => item.UpdatedAt))
            .FirstOrDefault();

        return group is null
            ? (null, null)
            : (
                group.FirstOrDefault(item => item.Key == DadataLegalEntityEnrichmentService.ApiKeySettingKey)?.Value,
                group.FirstOrDefault(item => item.Key == DadataLegalEntityEnrichmentService.SecretSettingKey)?.Value
            );
    }

    private async Task UpsertSettingAsync(string key, string value, DateTime now, CancellationToken ct)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == key, ct);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = now;
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string MaskSecret(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        var suffixLength = Math.Min(4, trimmed.Length);
        return $"••••{trimmed[^suffixLength..]}";
    }
}

public sealed class DadataIntegrationSettingsDto
{
    public bool ApiKeyConfigured { get; init; }
    public bool SecretConfigured { get; init; }
    public string ApiKeyMasked { get; init; } = string.Empty;
    public string SecretMasked { get; init; } = string.Empty;
    public bool CanEdit { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public sealed class UpdateDadataIntegrationSettingsRequest
{
    public string? ApiKey { get; init; }
    public string? Secret { get; init; }
}
