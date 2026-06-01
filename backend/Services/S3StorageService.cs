using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public interface IS3StorageService
{
    Task<S3StorageConnectionsOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<S3StorageConnectionDto> UpsertConnectionAsync(UpsertS3StorageConnectionRequest request, CancellationToken cancellationToken = default);
    Task DeleteConnectionAsync(string id, CancellationToken cancellationToken = default);
    Task<S3StorageConnectionTestDto> TestConnectionAsync(string id, TestS3StorageConnectionRequest request, CancellationToken cancellationToken = default);
    Task<S3StorageListResponseDto> ListObjectsAsync(string id, string? path, int limit, int offset, CancellationToken cancellationToken = default);
    Task<S3StorageOperationResultDto> CreateFolderAsync(string id, CreateS3StorageFolderRequest request, CancellationToken cancellationToken = default);
    Task<S3StorageOperationResultDto> UploadAsync(string id, string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default);
    Task<S3StorageDownloadResult> DownloadAsync(string id, string? path, CancellationToken cancellationToken = default);
    Task<S3StorageOperationResultDto> DeleteObjectAsync(string id, string? path, bool recursive, CancellationToken cancellationToken = default);
    Task<S3StorageOperationResultDto> CopyObjectAsync(string id, S3StorageObjectOperationRequest request, CancellationToken cancellationToken = default);
    Task<S3StorageOperationResultDto> MoveObjectAsync(string id, S3StorageObjectOperationRequest request, CancellationToken cancellationToken = default);
    Task<S3StorageTextFileDto> GetTextFileAsync(string id, string? path, CancellationToken cancellationToken = default);
    Task<S3StorageOperationResultDto> SaveTextFileAsync(string id, UpdateS3StorageTextFileRequest request, CancellationToken cancellationToken = default);
}

public interface IS3StorageClientFactory
{
    IS3StorageClient Create(S3StorageConnectionSettings settings);
}

public interface IS3StorageClient : IAsyncDisposable
{
    Task ListAsync(string bucket, string prefix, CancellationToken cancellationToken);
    Task<S3StorageClientListResult> ListObjectsAsync(string bucket, string prefix, string delimiter, CancellationToken cancellationToken);
    Task<List<string>> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken);
    Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken);
    Task PutTextAsync(string bucket, string key, string content, CancellationToken cancellationToken);
    Task<S3StorageDownloadedObject> GetAsync(string bucket, string key, CancellationToken cancellationToken);
    Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken);
    Task DeleteManyAsync(string bucket, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
    Task CopyAsync(string bucket, string fromKey, string toKey, CancellationToken cancellationToken);
}

public sealed record S3StorageClientObject(
    string Key,
    long Size,
    DateTime? LastModified,
    string? ContentType);

public sealed record S3StorageClientListResult(
    IReadOnlyList<string> CommonPrefixes,
    IReadOnlyList<S3StorageClientObject> Objects);

public sealed record S3StorageDownloadedObject(
    MemoryStream Content,
    string ContentType,
    string FileName);

public sealed record S3StorageDownloadResult(
    MemoryStream Content,
    string ContentType,
    string FileName,
    string Path,
    string Key);

public sealed class S3StorageOperationException : Exception
{
    public S3StorageOperationException(int statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class S3StorageService : IS3StorageService
{
    public const string HostkeyProviderKey = "hostkey";
    public const string HostkeyEndpoint = "https://s3-nl.hostkey.com";
    public const string HostkeyRegion = "nl";

    private const string ConnectionsSettingKey = "integrations.s3.connections";
    private const string DefaultProviderKey = HostkeyProviderKey;
    private const string DefaultRootPrefix = "";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly IS3StorageClientFactory _clientFactory;

    public S3StorageService(PaymentContext db, IS3StorageClientFactory clientFactory)
    {
        _db = db;
        _clientFactory = clientFactory;
    }

    public async Task<S3StorageConnectionsOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var connections = await LoadConnectionsAsync(cancellationToken);
        return new S3StorageConnectionsOverviewDto
        {
            ProviderPresets = BuildProviderPresets(),
            Connections = connections.Select(ToDto).ToList(),
        };
    }

    public async Task<S3StorageConnectionDto> UpsertConnectionAsync(
        UpsertS3StorageConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var connections = await LoadConnectionsAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var normalizedProvider = NormalizeProvider(request.Provider);
        var normalizedEndpoint = NormalizeEndpoint(request.Endpoint, normalizedProvider);
        var normalizedRegion = NormalizeRegion(request.Region, normalizedProvider);
        var normalizedBucket = NormalizeBucket(request.Bucket);
        var normalizedRootPrefix = NormalizeOptionalPrefix(request.RootPrefix, DefaultRootPrefix);
        var normalizedName = NormalizeName(request.Name, normalizedBucket);
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id.Trim();
        var existing = connections.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        var secret = request.ClearSecret
            ? string.Empty
            : string.IsNullOrWhiteSpace(request.SecretAccessKey)
                ? existing?.SecretAccessKey ?? string.Empty
                : request.SecretAccessKey.Trim();
        var accessKey = string.IsNullOrWhiteSpace(request.AccessKeyId)
            ? existing?.AccessKeyId ?? string.Empty
            : request.AccessKeyId.Trim();

        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new S3StorageOperationException(400, "Укажите Access Key для S3-хранилища.");
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new S3StorageOperationException(400, "Укажите Secret Key для S3-хранилища.");
        }

        var updated = new S3StorageConnectionSettings
        {
            Id = id,
            Name = normalizedName,
            Provider = normalizedProvider,
            Endpoint = normalizedEndpoint,
            Region = normalizedRegion,
            Bucket = normalizedBucket,
            RootPrefix = normalizedRootPrefix,
            AccessKeyId = accessKey,
            SecretAccessKey = secret,
            IsEnabled = request.IsEnabled,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        if (existing is null)
        {
            connections.Add(updated);
        }
        else
        {
            var index = connections.FindIndex(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            connections[index] = updated;
        }

        await SaveConnectionsAsync(connections, cancellationToken);
        return ToDto(updated);
    }

    public async Task DeleteConnectionAsync(string id, CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeRequiredId(id);
        var connections = await LoadConnectionsAsync(cancellationToken);
        var removed = connections.RemoveAll(item => string.Equals(item.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new S3StorageOperationException(404, "S3-подключение не найдено.");
        }

        await SaveConnectionsAsync(connections, cancellationToken);
    }

    public async Task<S3StorageConnectionTestDto> TestConnectionAsync(
        string id,
        TestS3StorageConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeRequiredId(id);
        var connections = await LoadConnectionsAsync(cancellationToken);
        var connection = connections.FirstOrDefault(item => string.Equals(item.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? throw new S3StorageOperationException(404, "S3-подключение не найдено.");

        if (!connection.IsEnabled)
        {
            throw new S3StorageOperationException(400, "S3-подключение отключено.");
        }

        EnsureConfigured(connection);

        var checkedAt = DateTime.UtcNow;
        var testPrefix = NormalizeOptionalPrefix(request.TestPrefix, "_payplanner-test");
        var markerName = $"payplanner-s3-test-{checkedAt:yyyyMMddHHmmss}.txt";
        var testKey = CombineKeys(connection.RootPrefix, testPrefix, markerName);
        var markerText = $"PayPlanner S3 connection test {checkedAt:O}";
        var listPrefix = CombineKeys(connection.RootPrefix, testPrefix);
        var steps = new List<S3StorageConnectionTestStepDto>();

        await using var client = _clientFactory.Create(connection);

        await RunS3StepAsync(
            steps,
            "list",
            "Проверка доступа к bucket",
            "Bucket доступен, список объектов прочитан.",
            () => client.ListAsync(connection.Bucket, listPrefix, cancellationToken));

        await RunS3StepAsync(
            steps,
            "upload",
            "Проверка загрузки файла",
            "Служебный файл загружен в S3-хранилище.",
            () => client.PutTextAsync(connection.Bucket, testKey, markerText, cancellationToken));

        await RunS3StepAsync(
            steps,
            "download",
            "Проверка чтения файла",
            "Служебный файл скачан и проверен.",
            async () =>
            {
                var downloaded = await client.GetAsync(connection.Bucket, testKey, cancellationToken);
                using var reader = new StreamReader(downloaded.Content, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(cancellationToken);
                if (!string.Equals(text, markerText, StringComparison.Ordinal))
                {
                    throw new S3StorageOperationException(502, "S3-хранилище вернуло служебный файл с неожиданным содержимым.");
                }
            });

        await RunS3StepAsync(
            steps,
            "cleanup",
            "Очистка служебного файла",
            "Служебный файл удален после проверки.",
            () => client.DeleteAsync(connection.Bucket, testKey, cancellationToken));

        return new S3StorageConnectionTestDto
        {
            Success = true,
            ConnectionId = connection.Id,
            Endpoint = connection.Endpoint,
            Region = connection.Region,
            Bucket = connection.Bucket,
            TestKey = testKey,
            CheckedAt = checkedAt,
            Steps = steps,
        };
    }

    public async Task<S3StorageListResponseDto> ListObjectsAsync(
        string id,
        string? path,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionAsync(id, cancellationToken);
        var relativePath = NormalizeRelativePath(path, allowEmpty: true);
        var prefix = EnsureTrailingSlash(CombineKeys(connection.RootPrefix, relativePath));
        var safeLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
        var safeOffset = Math.Max(0, offset);

        await using var client = _clientFactory.Create(connection);
        var result = await client.ListObjectsAsync(connection.Bucket, prefix, "/", cancellationToken);
        var items = result.CommonPrefixes
            .Select(prefixKey => BuildFolderDto(connection.RootPrefix, prefixKey))
            .Concat(result.Objects
                .Where(item => !string.Equals(item.Key, prefix, StringComparison.Ordinal) && !item.Key.EndsWith("/", StringComparison.Ordinal))
                .Select(item => BuildFileDto(connection.RootPrefix, item)))
            .OrderBy(item => item.Type == "folder" ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new S3StorageListResponseDto
        {
            Current = new S3StorageObjectDto
            {
                Name = string.IsNullOrWhiteSpace(relativePath) ? "S3" : Path.GetFileName(relativePath),
                Path = ToDisplayPath(relativePath),
                Key = prefix.TrimEnd('/'),
                Type = "folder",
            },
            Items = items.Skip(safeOffset).Take(safeLimit).ToList(),
            Limit = safeLimit,
            Offset = safeOffset,
            Total = items.Count,
        };
    }

    public async Task<S3StorageOperationResultDto> CreateFolderAsync(
        string id,
        CreateS3StorageFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionAsync(id, cancellationToken);
        var relativePath = NormalizeRelativePath(request.Path, allowEmpty: false);
        var key = EnsureTrailingSlash(CombineKeys(connection.RootPrefix, relativePath));

        await using var client = _clientFactory.Create(connection);
        if (await client.ExistsAsync(connection.Bucket, key, cancellationToken))
        {
            throw new S3StorageOperationException(409, "Папка S3 уже существует.");
        }

        await using var stream = new MemoryStream(Array.Empty<byte>());
        await client.PutAsync(connection.Bucket, key, stream, "application/x-directory", cancellationToken);
        return BuildOperationResult(connection.RootPrefix, key);
    }

    public async Task<S3StorageOperationResultDto> UploadAsync(
        string id,
        string? path,
        Stream content,
        string? contentType,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionAsync(id, cancellationToken);
        var relativePath = NormalizeRelativePath(path, allowEmpty: false);
        if (relativePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new S3StorageOperationException(400, "Укажите имя файла для загрузки в S3.");
        }

        var key = CombineKeys(connection.RootPrefix, relativePath);
        await using var client = _clientFactory.Create(connection);
        if (!overwrite && await client.ExistsAsync(connection.Bucket, key, cancellationToken))
        {
            throw new S3StorageOperationException(409, "Файл S3 уже существует.");
        }

        await client.PutAsync(connection.Bucket, key, content, NormalizeContentType(contentType, relativePath), cancellationToken);
        return BuildOperationResult(connection.RootPrefix, key);
    }

    public async Task<S3StorageDownloadResult> DownloadAsync(string id, string? path, CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionAsync(id, cancellationToken);
        var relativePath = NormalizeRelativePath(path, allowEmpty: false);
        var key = CombineKeys(connection.RootPrefix, relativePath);

        await using var client = _clientFactory.Create(connection);
        var downloaded = await client.GetAsync(connection.Bucket, key, cancellationToken);
        return new S3StorageDownloadResult(
            downloaded.Content,
            downloaded.ContentType,
            string.IsNullOrWhiteSpace(downloaded.FileName) ? Path.GetFileName(relativePath) : downloaded.FileName,
            ToDisplayPath(relativePath),
            key);
    }

    public async Task<S3StorageOperationResultDto> DeleteObjectAsync(
        string id,
        string? path,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionAsync(id, cancellationToken);
        var relativePath = NormalizeRelativePath(path, allowEmpty: false);
        var key = CombineKeys(connection.RootPrefix, relativePath);

        await using var client = _clientFactory.Create(connection);
        if (await client.ExistsAsync(connection.Bucket, key, cancellationToken))
        {
            await client.DeleteAsync(connection.Bucket, key, cancellationToken);
            return BuildOperationResult(connection.RootPrefix, key);
        }

        var prefix = EnsureTrailingSlash(key);
        var keys = await client.ListKeysAsync(connection.Bucket, prefix, cancellationToken);
        if (keys.Count == 0)
        {
            throw new S3StorageOperationException(404, "Объект S3 не найден.");
        }

        if (!recursive)
        {
            throw new S3StorageOperationException(409, "Папка S3 не пустая. Подтвердите рекурсивное удаление.");
        }

        await client.DeleteManyAsync(connection.Bucket, keys, cancellationToken);
        return BuildOperationResult(connection.RootPrefix, prefix);
    }

    public Task<S3StorageOperationResultDto> CopyObjectAsync(
        string id,
        S3StorageObjectOperationRequest request,
        CancellationToken cancellationToken = default)
        => CopyOrMoveObjectAsync(id, request, move: false, cancellationToken);

    public Task<S3StorageOperationResultDto> MoveObjectAsync(
        string id,
        S3StorageObjectOperationRequest request,
        CancellationToken cancellationToken = default)
        => CopyOrMoveObjectAsync(id, request, move: true, cancellationToken);

    public async Task<S3StorageTextFileDto> GetTextFileAsync(string id, string? path, CancellationToken cancellationToken = default)
    {
        var file = await DownloadAsync(id, path, cancellationToken);
        using var reader = new StreamReader(file.Content, Encoding.UTF8);
        return new S3StorageTextFileDto
        {
            Path = file.Path,
            Content = await reader.ReadToEndAsync(cancellationToken),
        };
    }

    public async Task<S3StorageOperationResultDto> SaveTextFileAsync(
        string id,
        UpdateS3StorageTextFileRequest request,
        CancellationToken cancellationToken = default)
    {
        var content = Encoding.UTF8.GetBytes(request.Content ?? string.Empty);
        await using var stream = new MemoryStream(content);
        return await UploadAsync(id, request.Path, stream, "text/plain; charset=utf-8", request.Overwrite, cancellationToken);
    }

    private async Task<S3StorageOperationResultDto> CopyOrMoveObjectAsync(
        string id,
        S3StorageObjectOperationRequest request,
        bool move,
        CancellationToken cancellationToken)
    {
        var connection = await GetEnabledConnectionAsync(id, cancellationToken);
        var fromPath = NormalizeRelativePath(request.FromPath, allowEmpty: false);
        var toPath = NormalizeRelativePath(request.ToPath, allowEmpty: false);
        var fromKey = CombineKeys(connection.RootPrefix, fromPath);
        var toKey = CombineKeys(connection.RootPrefix, toPath);

        await using var client = _clientFactory.Create(connection);
        if (await client.ExistsAsync(connection.Bucket, fromKey, cancellationToken))
        {
            if (!request.Overwrite && await client.ExistsAsync(connection.Bucket, toKey, cancellationToken))
            {
                throw new S3StorageOperationException(409, "Целевой объект S3 уже существует.");
            }

            await client.CopyAsync(connection.Bucket, fromKey, toKey, cancellationToken);
            if (move)
            {
                await client.DeleteAsync(connection.Bucket, fromKey, cancellationToken);
            }

            return BuildOperationResult(connection.RootPrefix, toKey);
        }

        var fromPrefix = EnsureTrailingSlash(fromKey);
        var toPrefix = EnsureTrailingSlash(toKey);
        var keys = await client.ListKeysAsync(connection.Bucket, fromPrefix, cancellationToken);
        if (keys.Count == 0)
        {
            throw new S3StorageOperationException(404, "Объект S3 не найден.");
        }

        if (!request.Overwrite)
        {
            foreach (var key in keys)
            {
                var destination = toPrefix + key[fromPrefix.Length..];
                if (await client.ExistsAsync(connection.Bucket, destination, cancellationToken))
                {
                    throw new S3StorageOperationException(409, "В целевой папке S3 уже есть объект с таким именем.");
                }
            }
        }

        foreach (var key in keys)
        {
            await client.CopyAsync(connection.Bucket, key, toPrefix + key[fromPrefix.Length..], cancellationToken);
        }

        if (move)
        {
            await client.DeleteManyAsync(connection.Bucket, keys, cancellationToken);
        }

        return BuildOperationResult(connection.RootPrefix, toPrefix);
    }

    private static async Task RunS3StepAsync(
        List<S3StorageConnectionTestStepDto> steps,
        string key,
        string name,
        string successMessage,
        Func<Task> action)
    {
        await action();
        steps.Add(new S3StorageConnectionTestStepDto
        {
            Key = key,
            Name = name,
            Success = true,
            Message = successMessage,
        });
    }

    private async Task<S3StorageConnectionSettings> GetEnabledConnectionAsync(string id, CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeRequiredId(id);
        var connections = await LoadConnectionsAsync(cancellationToken);
        var connection = connections.FirstOrDefault(item => string.Equals(item.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? throw new S3StorageOperationException(404, "S3-подключение не найдено.");

        if (!connection.IsEnabled)
        {
            throw new S3StorageOperationException(400, "S3-подключение отключено.");
        }

        EnsureConfigured(connection);
        return connection;
    }

    private static S3StorageObjectDto BuildFolderDto(string rootPrefix, string key)
    {
        var relativePath = StripRootPrefix(rootPrefix, key).Trim('/');
        return new S3StorageObjectDto
        {
            Name = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Папка",
            Path = ToDisplayPath(relativePath),
            Key = key.TrimEnd('/'),
            Type = "folder",
        };
    }

    private static S3StorageObjectDto BuildFileDto(string rootPrefix, S3StorageClientObject item)
    {
        var relativePath = StripRootPrefix(rootPrefix, item.Key);
        return new S3StorageObjectDto
        {
            Name = Path.GetFileName(relativePath),
            Path = ToDisplayPath(relativePath),
            Key = item.Key,
            Type = "file",
            ContentType = item.ContentType ?? NormalizeContentType(null, relativePath),
            Size = item.Size,
            LastModified = item.LastModified,
        };
    }

    private static S3StorageOperationResultDto BuildOperationResult(string rootPrefix, string key)
        => new()
        {
            Success = true,
            Path = ToDisplayPath(StripRootPrefix(rootPrefix, key).Trim('/')),
            Key = key.TrimEnd('/'),
        };

    private async Task<List<S3StorageConnectionSettings>> LoadConnectionsAsync(CancellationToken cancellationToken)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == ConnectionsSettingKey, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<S3StorageConnectionSettings>>(setting.Value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveConnectionsAsync(List<S3StorageConnectionSettings> connections, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == ConnectionsSettingKey, cancellationToken);
        var value = JsonSerializer.Serialize(connections, JsonOptions);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                Key = ConnectionsSettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static List<S3StorageProviderPresetDto> BuildProviderPresets()
        =>
        [
            new()
            {
                Key = HostkeyProviderKey,
                Name = "HOSTKEY S3",
                Endpoint = HostkeyEndpoint,
                Region = HostkeyRegion,
                ForcePathStyle = true,
            },
            new()
            {
                Key = "custom",
                Name = "S3-совместимое хранилище",
                Endpoint = string.Empty,
                Region = string.Empty,
                ForcePathStyle = true,
            },
        ];

    private static S3StorageConnectionDto ToDto(S3StorageConnectionSettings connection)
        => new()
        {
            Id = connection.Id,
            Name = connection.Name,
            Provider = connection.Provider,
            ProviderName = string.Equals(connection.Provider, HostkeyProviderKey, StringComparison.OrdinalIgnoreCase)
                ? "HOSTKEY S3"
                : "S3-совместимое хранилище",
            Endpoint = connection.Endpoint,
            Region = connection.Region,
            Bucket = connection.Bucket,
            RootPrefix = connection.RootPrefix,
            IsEnabled = connection.IsEnabled,
            IsConfigured = !string.IsNullOrWhiteSpace(connection.AccessKeyId)
                && !string.IsNullOrWhiteSpace(connection.SecretAccessKey)
                && !string.IsNullOrWhiteSpace(connection.Bucket),
            AccessKeyPreview = BuildSecretPreview(connection.AccessKeyId),
            SecretKeyPreview = BuildSecretPreview(connection.SecretAccessKey),
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
        };

    private static void EnsureConfigured(S3StorageConnectionSettings connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Endpoint)
            || string.IsNullOrWhiteSpace(connection.Region)
            || string.IsNullOrWhiteSpace(connection.Bucket)
            || string.IsNullOrWhiteSpace(connection.AccessKeyId)
            || string.IsNullOrWhiteSpace(connection.SecretAccessKey))
        {
            throw new S3StorageOperationException(400, "Заполните endpoint, регион, bucket, Access Key и Secret Key S3-хранилища.");
        }
    }

    private static string NormalizeRequiredId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new S3StorageOperationException(400, "Укажите S3-подключение.");
        }

        return value.Trim();
    }

    private static string NormalizeName(string? value, string bucket)
        => string.IsNullOrWhiteSpace(value) ? bucket : value.Trim()[..Math.Min(value.Trim().Length, 120)];

    private static string NormalizeProvider(string? value)
    {
        var provider = string.IsNullOrWhiteSpace(value) ? DefaultProviderKey : value.Trim().ToLowerInvariant();
        return provider switch
        {
            HostkeyProviderKey => HostkeyProviderKey,
            "custom" => "custom",
            _ => "custom",
        };
    }

    private static string NormalizeEndpoint(string? value, string provider)
    {
        var endpoint = string.IsNullOrWhiteSpace(value) && string.Equals(provider, HostkeyProviderKey, StringComparison.OrdinalIgnoreCase)
            ? HostkeyEndpoint
            : value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new S3StorageOperationException(400, "Укажите endpoint S3-хранилища.");
        }

        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = $"https://{endpoint}";
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new S3StorageOperationException(400, "Endpoint S3-хранилища должен быть корректным HTTP/HTTPS адресом.");
        }

        return endpoint.TrimEnd('/');
    }

    private static string NormalizeRegion(string? value, string provider)
    {
        var region = string.IsNullOrWhiteSpace(value) && string.Equals(provider, HostkeyProviderKey, StringComparison.OrdinalIgnoreCase)
            ? HostkeyRegion
            : value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region))
        {
            throw new S3StorageOperationException(400, "Укажите регион S3-хранилища.");
        }

        return region.Length > 64 ? region[..64] : region;
    }

    private static string NormalizeBucket(string? value)
    {
        var bucket = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new S3StorageOperationException(400, "Укажите bucket S3-хранилища.");
        }

        if (bucket.Contains('/') || bucket.Contains('\\') || bucket.Contains("..", StringComparison.Ordinal))
        {
            throw new S3StorageOperationException(400, "Bucket S3-хранилища указан некорректно.");
        }

        return bucket.Length > 63 ? bucket[..63] : bucket;
    }

    private static string NormalizeOptionalPrefix(string? value, string fallback)
    {
        var prefix = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('\\', '/');
        prefix = prefix.Trim('/');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var parts = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => part == "." || part == ".."))
        {
            throw new S3StorageOperationException(400, "Путь S3 не должен содержать переходы между каталогами.");
        }

        return string.Join('/', parts).Length > 512
            ? string.Join('/', parts)[..512].Trim('/')
            : string.Join('/', parts);
    }

    private static string NormalizeRelativePath(string? value, bool allowEmpty)
    {
        var path = (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw new S3StorageOperationException(400, "Укажите путь объекта S3.");
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => part == "." || part == ".."))
        {
            throw new S3StorageOperationException(400, "Путь S3 не должен содержать переходы между каталогами.");
        }

        return string.Join('/', parts);
    }

    private static string CombineKeys(params string?[] parts)
        => string.Join('/', parts
            .Select(part => NormalizeOptionalPrefix(part, string.Empty))
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string EnsureTrailingSlash(string value)
        => string.IsNullOrWhiteSpace(value) || value.EndsWith("/", StringComparison.Ordinal)
            ? value
            : $"{value}/";

    private static string StripRootPrefix(string rootPrefix, string key)
    {
        var normalizedRoot = NormalizeOptionalPrefix(rootPrefix, string.Empty);
        var normalizedKey = key.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return normalizedKey;
        }

        return normalizedKey.StartsWith($"{normalizedRoot}/", StringComparison.Ordinal)
            ? normalizedKey[(normalizedRoot.Length + 1)..]
            : string.Equals(normalizedKey, normalizedRoot, StringComparison.Ordinal)
                ? string.Empty
                : normalizedKey;
    }

    private static string ToDisplayPath(string relativePath)
        => string.IsNullOrWhiteSpace(relativePath) ? "/" : $"/{relativePath.Trim('/')}";

    private static string NormalizeContentType(string? contentType, string path)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            return contentType.Trim();
        }

        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(path, out var detected)
            ? detected
            : "application/octet-stream";
    }

    private static string? BuildSecretPreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 8 ? "********" : $"{trimmed[..4]}...{trimmed[^4..]}";
    }
}

public sealed class S3StorageClientFactory : IS3StorageClientFactory
{
    public IS3StorageClient Create(S3StorageConnectionSettings settings)
    {
        var credentials = new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey);
        var config = new AmazonS3Config
        {
            ServiceURL = settings.Endpoint,
            AuthenticationRegion = settings.Region,
            ForcePathStyle = true,
        };

        return new AwsS3StorageClient(new AmazonS3Client(credentials, config));
    }
}

public sealed class AwsS3StorageClient : IS3StorageClient
{
    private readonly IAmazonS3 _client;

    public AwsS3StorageClient(IAmazonS3 client)
    {
        _client = client;
    }

    public async Task ListAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                MaxKeys = 1,
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task<S3StorageClientListResult> ListObjectsAsync(string bucket, string prefix, string delimiter, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                Delimiter = delimiter,
                MaxKeys = 1000,
            }, cancellationToken);

            return new S3StorageClientListResult(
                response.CommonPrefixes ?? [],
                (response.S3Objects ?? [])
                    .Select(item => new S3StorageClientObject(
                        item.Key,
                        item.Size ?? 0,
                        item.LastModified,
                        null))
                    .ToList());
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task<List<string>> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            var keys = new List<string>();
            string? token = null;
            do
            {
                var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    ContinuationToken = token,
                }, cancellationToken);

                keys.AddRange((response.S3Objects ?? []).Select(item => item.Key));
                token = response.IsTruncated == true ? response.NextContinuationToken : null;
            }
            while (!string.IsNullOrWhiteSpace(token));

            return keys;
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
            }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task PutTextAsync(string bucket, string key, string content, CancellationToken cancellationToken)
    {
        try
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = content,
                ContentType = "text/plain; charset=utf-8",
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task<S3StorageDownloadedObject> GetAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
            }, cancellationToken);
            var memory = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            return new S3StorageDownloadedObject(
                memory,
                string.IsNullOrWhiteSpace(response.Headers.ContentType) ? "application/octet-stream" : response.Headers.ContentType,
                Path.GetFileName(key));
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task DeleteManyAsync(string bucket, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var chunk in keys.Chunk(1000))
            {
                await _client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = chunk.Select(key => new KeyVersion { Key = key }).ToList(),
                }, cancellationToken);
            }
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public async Task CopyAsync(string bucket, string fromKey, string toKey, CancellationToken cancellationToken)
    {
        try
        {
            await _client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = fromKey,
                DestinationBucket = bucket,
                DestinationKey = toKey,
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw MapException(ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static S3StorageOperationException MapException(AmazonS3Exception ex)
    {
        var statusCode = ex.StatusCode == 0 ? 502 : (int)ex.StatusCode;
        var message = statusCode switch
        {
            (int)HttpStatusCode.Unauthorized => "S3-хранилище отклонило Access Key или Secret Key. Проверьте учетные данные.",
            (int)HttpStatusCode.Forbidden => "S3-хранилище вернуло отказ доступа. Проверьте права ключа и bucket.",
            (int)HttpStatusCode.NotFound => "S3 bucket или объект не найден. Проверьте имя bucket и префикс.",
            (int)HttpStatusCode.ServiceUnavailable => "S3-хранилище временно недоступно. Повторите операцию позже.",
            _ => string.IsNullOrWhiteSpace(ex.Message)
                ? $"S3-хранилище вернуло ошибку {statusCode}."
                : ex.Message,
        };

        return new S3StorageOperationException(statusCode, message, ex);
    }
}

public sealed class S3StorageConnectionSettings
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string RootPrefix { get; init; } = string.Empty;
    public string AccessKeyId { get; init; } = string.Empty;
    public string SecretAccessKey { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
