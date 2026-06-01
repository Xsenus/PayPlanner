using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
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
}

public interface IS3StorageClientFactory
{
    IS3StorageClient Create(S3StorageConnectionSettings settings);
}

public interface IS3StorageClient : IAsyncDisposable
{
    Task ListAsync(string bucket, string prefix, CancellationToken cancellationToken);
    Task PutTextAsync(string bucket, string key, string content, CancellationToken cancellationToken);
    Task<string> GetTextAsync(string bucket, string key, CancellationToken cancellationToken);
    Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken);
}

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
    private const string DefaultRootPrefix = "payplanner";
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
                var downloaded = await client.GetTextAsync(connection.Bucket, testKey, cancellationToken);
                if (!string.Equals(downloaded, markerText, StringComparison.Ordinal))
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

    private static string CombineKeys(params string?[] parts)
        => string.Join('/', parts
            .Select(part => NormalizeOptionalPrefix(part, string.Empty))
            .Where(part => !string.IsNullOrWhiteSpace(part)));

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

    public async Task<string> GetTextAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
            }, cancellationToken);
            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
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
