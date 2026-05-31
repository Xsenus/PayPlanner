using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public interface IYandexDiskService
{
    Task<YandexDiskSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<YandexDiskSettingsDto> UpdateSettingsAsync(UpdateYandexDiskSettingsRequest request, CancellationToken cancellationToken = default);
    Task<YandexDiskListResponseDto> ListAsync(string? path, int limit, int offset, CancellationToken cancellationToken = default);
    Task<YandexDiskOperationResultDto> CreateFolderAsync(string? path, CancellationToken cancellationToken = default);
    Task<YandexDiskOperationResultDto> CopyAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<YandexDiskOperationResultDto> MoveAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<YandexDiskOperationResultDto> DeleteAsync(string? path, bool permanently, CancellationToken cancellationToken = default);
    Task<YandexDiskOperationResultDto> UploadAsync(string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default);
    Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken cancellationToken = default);
    Task<YandexDiskTextFileDto> GetTextFileAsync(string? path, CancellationToken cancellationToken = default);
    Task<YandexDiskOperationResultDto> SaveTextFileAsync(UpdateYandexDiskTextFileRequest request, CancellationToken cancellationToken = default);
    Task<YandexDiskCrmSyncSettingsDto> GetCrmSyncSettingsAsync(CancellationToken cancellationToken = default);
    Task<YandexDiskCrmSyncSettingsDto> UpdateCrmSyncSettingsAsync(UpdateYandexDiskCrmSyncSettingsRequest request, CancellationToken cancellationToken = default);
    Task<YandexDiskOperationResultDto> UploadCrmDocumentAsync(YandexDiskCrmDocumentUpload upload, CancellationToken cancellationToken = default);
}

public sealed record YandexDiskDownload(string FileName, string ContentType, byte[] Content);

public sealed record YandexDiskCrmDocumentUpload(
    Stream Content,
    string? ContentType,
    string OriginalFileName,
    string DocumentKind,
    string DocumentType,
    string? DocumentNumber,
    DateTime? DocumentDate,
    string DocumentTitle,
    string? ClientName,
    string? LegalEntityName,
    string? CaseNumber,
    string? CaseTitle,
    bool? OverwriteExisting = null);

public sealed class YandexDiskOperationException : Exception
{
    public YandexDiskOperationException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class YandexDiskService : IYandexDiskService
{
    private const string OAuthTokenSettingKey = "integrations.yandexDisk.oauthToken";
    private const string RootPathSettingKey = "integrations.yandexDisk.rootPath";
    private const string CrmSyncSettingsKey = "integrations.yandexDisk.crmSync.settings";
    private const string DefaultRootPath = "disk:/PayPlanner";
    private const string DefaultFolderTemplate = "CRM/{clientName}/{caseNumber}/{documentType}";
    private const string DefaultFileNameTemplate = "{documentDate}-{documentNumber}-{documentTitle}";
    private const string ApiBaseUrl = "https://cloud-api.yandex.net/v1/disk";
    private const int MaxTextFileBytes = 2 * 1024 * 1024;
    private static readonly string[] CrmSyncTemplateTokens =
    [
        "clientName",
        "legalEntityName",
        "caseNumber",
        "caseTitle",
        "documentType",
        "documentNumber",
        "documentDate",
        "documentTitle"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly HttpClient _httpClient;

    public YandexDiskService(PaymentContext db, HttpClient httpClient)
    {
        _db = db;
        _httpClient = httpClient;
    }

    public async Task<YandexDiskSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        return BuildSettingsDto(settings);
    }

    public async Task<YandexDiskSettingsDto> UpdateSettingsAsync(UpdateYandexDiskSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var rootPath = NormalizeRootPath(request.RootPath);
        await UpsertSettingAsync(RootPathSettingKey, rootPath, now, cancellationToken);

        if (request.ClearToken)
        {
            await RemoveSettingAsync(OAuthTokenSettingKey, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.OAuthToken))
        {
            await UpsertSettingAsync(OAuthTokenSettingKey, request.OAuthToken.Trim(), now, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<YandexDiskListResponseDto> ListAsync(string? path, int limit, int offset, CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var normalizedLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
        var normalizedOffset = Math.Max(offset, 0);
        var fullPath = BuildFullPath(settings.RootPath, NormalizeRelativePath(path));
        var token = settings.OAuthToken!;
        var resource = await SendJsonAsync<YandexDiskResourcePayload>(
            HttpMethod.Get,
            $"{ApiBaseUrl}/resources?path={Uri.EscapeDataString(fullPath)}&limit={normalizedLimit}&offset={normalizedOffset}",
            token,
            null,
            cancellationToken);

        return new YandexDiskListResponseDto
        {
            Current = MapResource(resource, settings.RootPath),
            Items = resource.Embedded?.Items?.Select(item => MapResource(item, settings.RootPath)).ToList() ?? [],
            Limit = resource.Embedded?.Limit ?? normalizedLimit,
            Offset = resource.Embedded?.Offset ?? normalizedOffset,
            Total = resource.Embedded?.Total ?? 0,
        };
    }

    public async Task<YandexDiskOperationResultDto> CreateFolderAsync(string? path, CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var relativePath = NormalizeRequiredRelativePath(path, "Укажите путь новой папки.");
        var fullPath = BuildFullPath(settings.RootPath, relativePath);
        var token = settings.OAuthToken!;
        await SendAsync(
            HttpMethod.Put,
            $"{ApiBaseUrl}/resources?path={Uri.EscapeDataString(fullPath)}",
            token,
            null,
            cancellationToken);

        return BuildResult(relativePath);
    }

    public Task<YandexDiskOperationResultDto> CopyAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
        => CopyOrMoveAsync("copy", fromPath, toPath, overwrite, cancellationToken);

    public Task<YandexDiskOperationResultDto> MoveAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
        => CopyOrMoveAsync("move", fromPath, toPath, overwrite, cancellationToken);

    public async Task<YandexDiskOperationResultDto> DeleteAsync(string? path, bool permanently, CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var relativePath = NormalizeRequiredRelativePath(path, "Выберите файл или папку для удаления.");
        var fullPath = BuildFullPath(settings.RootPath, relativePath);
        var token = settings.OAuthToken!;
        await SendAsync(
            HttpMethod.Delete,
            $"{ApiBaseUrl}/resources?path={Uri.EscapeDataString(fullPath)}&permanently={permanently.ToString().ToLowerInvariant()}",
            token,
            null,
            cancellationToken);

        return BuildResult(relativePath);
    }

    public async Task<YandexDiskOperationResultDto> UploadAsync(
        string? path,
        Stream content,
        string? contentType,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var relativePath = NormalizeRequiredRelativePath(path, "Укажите путь файла для загрузки.");
        var fullPath = BuildFullPath(settings.RootPath, relativePath);
        var token = settings.OAuthToken!;
        var link = await SendJsonAsync<YandexDiskLinkPayload>(
            HttpMethod.Get,
            $"{ApiBaseUrl}/resources/upload?path={Uri.EscapeDataString(fullPath)}&overwrite={overwrite.ToString().ToLowerInvariant()}",
            token,
            null,
            cancellationToken);

        using var streamContent = new StreamContent(content);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        using var uploadResponse = await _httpClient.PutAsync(link.Href, streamContent, cancellationToken);
        await EnsureSuccessAsync(uploadResponse, cancellationToken);
        return BuildResult(relativePath);
    }

    public async Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var relativePath = NormalizeRequiredRelativePath(path, "Выберите файл для скачивания.");
        var fullPath = BuildFullPath(settings.RootPath, relativePath);
        var token = settings.OAuthToken!;
        var link = await SendJsonAsync<YandexDiskLinkPayload>(
            HttpMethod.Get,
            $"{ApiBaseUrl}/resources/download?path={Uri.EscapeDataString(fullPath)}",
            token,
            null,
            cancellationToken);

        using var response = await _httpClient.GetAsync(link.Href, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        var fileName = Path.GetFileName(relativePath.Replace('\\', '/'));
        return new YandexDiskDownload(
            string.IsNullOrWhiteSpace(fileName) ? "yandex-disk-file" : fileName,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            buffer.ToArray());
    }

    public async Task<YandexDiskTextFileDto> GetTextFileAsync(string? path, CancellationToken cancellationToken = default)
    {
        var download = await DownloadAsync(path, cancellationToken);
        if (download.Content.Length > MaxTextFileBytes)
        {
            throw new YandexDiskOperationException(400, "Файл слишком большой для встроенного текстового редактора.");
        }

        return new YandexDiskTextFileDto
        {
            Path = NormalizeDisplayPath(path),
            Content = DecodeText(download.Content),
        };
    }

    public async Task<YandexDiskOperationResultDto> SaveTextFileAsync(UpdateYandexDiskTextFileRequest request, CancellationToken cancellationToken = default)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(request.Content ?? string.Empty);
        await using var stream = new MemoryStream(bytes);
        return await UploadAsync(request.Path, stream, "text/plain; charset=utf-8", request.Overwrite, cancellationToken);
    }

    public async Task<YandexDiskCrmSyncSettingsDto> GetCrmSyncSettingsAsync(CancellationToken cancellationToken = default)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == CrmSyncSettingsKey, cancellationToken);

        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return BuildDefaultCrmSyncSettings();
        }

        try
        {
            var stored = JsonSerializer.Deserialize<YandexDiskCrmSyncSettingsDto>(setting.Value, JsonOptions);
            if (stored is not null)
            {
                var normalized = NormalizeCrmSyncSettings(stored);
                normalized.UpdatedAt = setting.UpdatedAt;
                return normalized;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON should not break the settings screen.
        }

        var defaults = BuildDefaultCrmSyncSettings();
        defaults.UpdatedAt = setting.UpdatedAt;
        return defaults;
    }

    public async Task<YandexDiskCrmSyncSettingsDto> UpdateCrmSyncSettingsAsync(
        UpdateYandexDiskCrmSyncSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var normalized = NormalizeCrmSyncSettings(new YandexDiskCrmSyncSettingsDto
        {
            Enabled = request.Enabled,
            SyncInvoices = request.SyncInvoices,
            SyncActs = request.SyncActs,
            SyncContracts = request.SyncContracts,
            SyncCaseDocuments = request.SyncCaseDocuments,
            SyncTaskFiles = request.SyncTaskFiles,
            OverwriteExisting = request.OverwriteExisting,
            FolderTemplate = request.FolderTemplate ?? string.Empty,
            FileNameTemplate = request.FileNameTemplate ?? string.Empty,
            UpdatedAt = now,
        });
        normalized.UpdatedAt = now;

        await UpsertSettingAsync(CrmSyncSettingsKey, JsonSerializer.Serialize(normalized, JsonOptions), now, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return normalized;
    }

    public async Task<YandexDiskOperationResultDto> UploadCrmDocumentAsync(
        YandexDiskCrmDocumentUpload upload,
        CancellationToken cancellationToken = default)
    {
        var syncSettings = await GetCrmSyncSettingsAsync(cancellationToken);
        if (!syncSettings.Enabled)
        {
            throw new YandexDiskOperationException(400, "Включите правила синхронизации CRM в разделе Я.Диск.");
        }

        if (!IsCrmDocumentKindEnabled(syncSettings, upload.DocumentKind))
        {
            throw new YandexDiskOperationException(400, "Для этого типа CRM-документа синхронизация с Яндекс.Диском отключена.");
        }

        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var tokens = BuildCrmDocumentTokens(upload);
        var folderPath = ApplyCrmTemplate(syncSettings.FolderTemplate, tokens, isFileName: false);
        var fileName = ApplyCrmTemplate(syncSettings.FileNameTemplate, tokens, isFileName: true);
        fileName = EnsureFileExtension(fileName, upload.OriginalFileName);
        var targetPath = string.IsNullOrWhiteSpace(folderPath) ? fileName : $"{folderPath}/{fileName}";

        await EnsureFolderPathAsync(settings, folderPath, cancellationToken);
        return await UploadAsync(targetPath, upload.Content, upload.ContentType, upload.OverwriteExisting ?? syncSettings.OverwriteExisting, cancellationToken);
    }

    private async Task<YandexDiskOperationResultDto> CopyOrMoveAsync(
        string operation,
        string? fromPath,
        string? toPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var fromRelative = NormalizeRequiredRelativePath(fromPath, "Выберите исходный файл или папку.");
        var toRelative = NormalizeRequiredRelativePath(toPath, "Укажите новый путь.");
        var fromFullPath = BuildFullPath(settings.RootPath, fromRelative);
        var toFullPath = BuildFullPath(settings.RootPath, toRelative);
        var token = settings.OAuthToken!;
        await SendAsync(
            HttpMethod.Post,
            $"{ApiBaseUrl}/resources/{operation}?from={Uri.EscapeDataString(fromFullPath)}&path={Uri.EscapeDataString(toFullPath)}&overwrite={overwrite.ToString().ToLowerInvariant()}",
            token,
            null,
            cancellationToken);

        return BuildResult(toRelative);
    }

    private async Task<YandexDiskSettingsState> LoadConfiguredSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.OAuthToken))
        {
            throw new YandexDiskOperationException(400, "Сначала сохраните OAuth-токен Яндекс.Диска в настройках раздела.");
        }

        return settings;
    }

    private async Task<YandexDiskSettingsState> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == OAuthTokenSettingKey || item.Key == RootPathSettingKey)
            .ToListAsync(cancellationToken);

        var tokenSetting = rows.FirstOrDefault(item => item.Key == OAuthTokenSettingKey);
        var rootSetting = rows.FirstOrDefault(item => item.Key == RootPathSettingKey);
        return new YandexDiskSettingsState(
            tokenSetting?.Value?.Trim(),
            NormalizeRootPath(rootSetting?.Value),
            new[] { tokenSetting?.UpdatedAt, rootSetting?.UpdatedAt }.Where(value => value.HasValue).Max());
    }

    private async Task UpsertSettingAsync(string key, string value, DateTime now, CancellationToken cancellationToken)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
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

    private async Task RemoveSettingAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (setting is not null)
        {
            _db.SystemSettings.Remove(setting);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        request.Content = content;
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new YandexDiskOperationException(502, $"Не удалось подключиться к Яндекс.Диску: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YandexDiskOperationException(504, $"Яндекс.Диск не ответил вовремя: {ex.Message}");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return response;
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string url, string token, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, url, token, content, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        return payload ?? throw new YandexDiskOperationException(502, "Яндекс.Диск вернул пустой ответ.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await ReadErrorMessageAsync(response, cancellationToken);
        response.Dispose();
        throw new YandexDiskOperationException((int)response.StatusCode, message);
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var error = JsonSerializer.Deserialize<YandexDiskErrorPayload>(text, JsonOptions);
                return error?.Message ?? error?.Description ?? text;
            }
            catch (JsonException)
            {
                return text;
            }
        }

        return $"Яндекс.Диск вернул ошибку {(int)response.StatusCode}.";
    }

    private static YandexDiskSettingsDto BuildSettingsDto(YandexDiskSettingsState settings)
        => new()
        {
            IsConfigured = !string.IsNullOrWhiteSpace(settings.OAuthToken),
            RootPath = settings.RootPath,
            TokenPreview = BuildTokenPreview(settings.OAuthToken),
            UpdatedAt = settings.UpdatedAt,
        };

    private static string? BuildTokenPreview(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        return trimmed.Length <= 8 ? "********" : $"{trimmed[..4]}...{trimmed[^4..]}";
    }

    private static string NormalizeRootPath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? DefaultRootPath : value.Trim().Replace('\\', '/');
        if (!path.StartsWith("disk:", StringComparison.OrdinalIgnoreCase))
        {
            path = path.StartsWith('/') ? $"disk:{path}" : $"disk:/{path}";
        }

        if (path.Equals("disk:", StringComparison.OrdinalIgnoreCase))
        {
            return "disk:/";
        }

        while (path.Length > "disk:/".Length && path.EndsWith('/'))
        {
            path = path[..^1];
        }

        return path;
    }

    private static string NormalizeRelativePath(string? value)
    {
        var path = (value ?? string.Empty).Trim().Replace('\\', '/');
        if (path.StartsWith("disk:", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..];
        }

        path = path.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var parts = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (parts.Any(part => part == "." || part == ".."))
        {
            throw new YandexDiskOperationException(400, "Путь не должен содержать переходы между каталогами.");
        }

        return string.Join('/', parts);
    }

    private static string NormalizeRequiredRelativePath(string? value, string message)
    {
        var path = NormalizeRelativePath(value);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new YandexDiskOperationException(400, message);
        }

        return path;
    }

    private static string BuildFullPath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return rootPath;
        }

        return rootPath.Equals("disk:/", StringComparison.OrdinalIgnoreCase)
            ? $"disk:/{relativePath}"
            : $"{rootPath}/{relativePath}";
    }

    private static YandexDiskResourceDto MapResource(YandexDiskResourcePayload resource, string rootPath)
    {
        var relativePath = ToRelativePath(resource.Path, rootPath);
        return new YandexDiskResourceDto
        {
            Name = resource.Name ?? (string.IsNullOrEmpty(relativePath) ? Path.GetFileName(rootPath) : Path.GetFileName(relativePath)),
            Path = string.IsNullOrWhiteSpace(relativePath) ? "/" : $"/{relativePath}",
            FullPath = resource.Path ?? string.Empty,
            Type = resource.Type ?? string.Empty,
            MimeType = resource.MimeType,
            Size = resource.Size,
            Created = resource.Created,
            Modified = resource.Modified,
            PublicUrl = resource.PublicUrl,
        };
    }

    private static string ToRelativePath(string? fullPath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        var normalizedRoot = NormalizeRootPath(rootPath);
        if (fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = normalizedRoot.EndsWith('/') ? normalizedRoot : $"{normalizedRoot}/";
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath[prefix.Length..]
            : fullPath.Replace("disk:/", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('/');
    }

    private static string NormalizeDisplayPath(string? path)
    {
        var relative = NormalizeRelativePath(path);
        return string.IsNullOrWhiteSpace(relative) ? "/" : $"/{relative}";
    }

    private static YandexDiskOperationResultDto BuildResult(string relativePath)
        => new()
        {
            Success = true,
            Path = string.IsNullOrWhiteSpace(relativePath) ? "/" : $"/{relativePath}",
        };

    private static YandexDiskCrmSyncSettingsDto BuildDefaultCrmSyncSettings()
        => NormalizeCrmSyncSettings(new YandexDiskCrmSyncSettingsDto
        {
            Enabled = false,
            SyncInvoices = true,
            SyncActs = true,
            SyncContracts = true,
            SyncCaseDocuments = true,
            SyncTaskFiles = false,
            OverwriteExisting = false,
            FolderTemplate = DefaultFolderTemplate,
            FileNameTemplate = DefaultFileNameTemplate,
        });

    private static YandexDiskCrmSyncSettingsDto NormalizeCrmSyncSettings(YandexDiskCrmSyncSettingsDto settings)
    {
        var folderTemplate = NormalizeTemplate(settings.FolderTemplate, DefaultFolderTemplate, 240);
        var fileNameTemplate = NormalizeTemplate(settings.FileNameTemplate, DefaultFileNameTemplate, 180);

        return new YandexDiskCrmSyncSettingsDto
        {
            Enabled = settings.Enabled,
            SyncInvoices = settings.SyncInvoices,
            SyncActs = settings.SyncActs,
            SyncContracts = settings.SyncContracts,
            SyncCaseDocuments = settings.SyncCaseDocuments,
            SyncTaskFiles = settings.SyncTaskFiles,
            OverwriteExisting = settings.OverwriteExisting,
            FolderTemplate = folderTemplate,
            FileNameTemplate = fileNameTemplate,
            AvailableTokens = CrmSyncTemplateTokens,
            UpdatedAt = settings.UpdatedAt,
        };
    }

    private static string NormalizeTemplate(string? value, string fallback, int maxLength)
    {
        var template = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('\\', '/');
        template = template.Replace("..", string.Empty, StringComparison.Ordinal).Trim('/');
        while (template.Contains("//", StringComparison.Ordinal))
        {
            template = template.Replace("//", "/", StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            template = fallback;
        }

        return template.Length > maxLength ? template[..maxLength] : template;
    }

    private async Task EnsureFolderPathAsync(YandexDiskSettingsState settings, string folderPath, CancellationToken cancellationToken)
    {
        var normalizedFolder = NormalizeRelativePath(folderPath);
        if (string.IsNullOrWhiteSpace(normalizedFolder))
        {
            return;
        }

        var token = settings.OAuthToken!;
        var accumulated = string.Empty;
        foreach (var part in normalizedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            accumulated = string.IsNullOrWhiteSpace(accumulated) ? part : $"{accumulated}/{part}";
            var fullPath = BuildFullPath(settings.RootPath, accumulated);
            try
            {
                await SendAsync(
                    HttpMethod.Put,
                    $"{ApiBaseUrl}/resources?path={Uri.EscapeDataString(fullPath)}",
                    token,
                    null,
                    cancellationToken);
            }
            catch (YandexDiskOperationException ex) when (ex.StatusCode == 409)
            {
                // Folder already exists.
            }
        }
    }

    private static bool IsCrmDocumentKindEnabled(YandexDiskCrmSyncSettingsDto settings, string kind)
        => kind switch
        {
            "invoice" => settings.SyncInvoices,
            "act" => settings.SyncActs,
            "contract" => settings.SyncContracts,
            "caseDocument" => settings.SyncCaseDocuments,
            "taskFile" => settings.SyncTaskFiles,
            _ => false,
        };

    private static Dictionary<string, string> BuildCrmDocumentTokens(YandexDiskCrmDocumentUpload upload)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["clientName"] = upload.ClientName ?? string.Empty,
            ["legalEntityName"] = upload.LegalEntityName ?? string.Empty,
            ["caseNumber"] = upload.CaseNumber ?? string.Empty,
            ["caseTitle"] = upload.CaseTitle ?? string.Empty,
            ["documentType"] = upload.DocumentType,
            ["documentNumber"] = upload.DocumentNumber ?? string.Empty,
            ["documentDate"] = upload.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["documentTitle"] = upload.DocumentTitle,
        };

    private static string ApplyCrmTemplate(string template, IReadOnlyDictionary<string, string> tokens, bool isFileName)
    {
        var value = template;
        foreach (var token in CrmSyncTemplateTokens)
        {
            var replacement = tokens.TryGetValue(token, out var tokenValue) ? tokenValue : string.Empty;
            value = value.Replace($"{{{token}}}", replacement, StringComparison.OrdinalIgnoreCase);
        }

        var segments = value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizePathSegment)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        if (segments.Count == 0)
        {
            return isFileName ? "document" : string.Empty;
        }

        return isFileName ? segments[^1] : string.Join('/', segments);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch);
        }

        var result = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "document" : result;
    }

    private static string EnsureFileExtension(string fileName, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension) || !string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            return fileName;
        }

        return $"{fileName}{extension}";
    }

    private static string DecodeText(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        return Encoding.UTF8.GetString(content);
    }

    private sealed record YandexDiskSettingsState(string? OAuthToken, string RootPath, DateTime? UpdatedAt);

    private sealed class YandexDiskLinkPayload
    {
        public string Href { get; set; } = string.Empty;
    }

    private sealed class YandexDiskResourcePayload
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Type { get; set; }
        [JsonPropertyName("mime_type")]
        public string? MimeType { get; set; }
        public long? Size { get; set; }
        public DateTime? Created { get; set; }
        public DateTime? Modified { get; set; }
        [JsonPropertyName("public_url")]
        public string? PublicUrl { get; set; }
        [JsonPropertyName("_embedded")]
        public YandexDiskEmbeddedPayload? Embedded { get; set; }
    }

    private sealed class YandexDiskEmbeddedPayload
    {
        public List<YandexDiskResourcePayload> Items { get; set; } = [];
        public int Limit { get; set; }
        public int Offset { get; set; }
        public int Total { get; set; }
    }

    private sealed class YandexDiskErrorPayload
    {
        public string? Message { get; set; }
        public string? Description { get; set; }
    }
}
