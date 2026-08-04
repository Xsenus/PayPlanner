using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public interface IMessengerDocumentDeliveryService
{
    Task<MessengerRecipientBinding> CreateVerifiedBindingAsync(
        int userId,
        int clientId,
        int? clientPortalAccountId,
        string channel,
        string provider,
        string displayName,
        string address,
        string verificationMethod,
        CancellationToken ct);

    Task<PreparedMessengerDocumentDelivery> PrepareCaseDocumentAsync(
        int userId,
        int documentId,
        int recipientBindingId,
        string? message,
        CancellationToken ct);

    Task<PreparedMessengerDocumentDelivery> PrepareGeneratedDocumentAsync(
        int userId,
        string documentType,
        int documentId,
        int documentVersion,
        int clientId,
        string originalFileName,
        string contentType,
        Stream source,
        long declaredSize,
        int recipientBindingId,
        string? message,
        CancellationToken ct);

    Task<MessengerDocumentDelivery> QueueAsync(int deliveryId, CancellationToken ct);

    Task<MessengerDocumentDownload?> ConsumeDownloadAsync(Guid publicId, string token, CancellationToken ct);
}

public sealed class MessengerDocumentDeliveryService : IMessengerDocumentDeliveryService
{
    private const long MaximumSnapshotBytes = 25L * 1024 * 1024;
    private static readonly HashSet<string> SupportedChannels = new(StringComparer.Ordinal)
    {
        MessengerDocumentChannels.Telegram,
        MessengerDocumentChannels.Max,
        MessengerDocumentChannels.WhatsAppBusiness,
    };

    private readonly PaymentContext _db;
    private readonly ICaseDocumentStorage _caseDocumentStorage;
    private readonly IDataProtector _addressProtector;
    private readonly string _snapshotRoot;
    private readonly bool _enabled;
    private readonly TimeSpan _linkLifetime;

    public MessengerDocumentDeliveryService(
        PaymentContext db,
        ICaseDocumentStorage caseDocumentStorage,
        IDataProtectionProvider dataProtectionProvider,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _db = db;
        _caseDocumentStorage = caseDocumentStorage;
        _addressProtector = dataProtectionProvider.CreateProtector("PayPlanner.MessengerDocumentDelivery.Address.v1");
        _enabled = configuration.GetValue("MessengerDocumentDelivery:Enabled", false);
        _linkLifetime = TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue("MessengerDocumentDelivery:LinkLifetimeHours", 24),
            1,
            168));
        _snapshotRoot = Path.Combine(
            ApplicationFileStoragePaths.ResolveRoot(environment, configuration),
            "messenger-document-deliveries");
    }

    public async Task<MessengerRecipientBinding> CreateVerifiedBindingAsync(
        int userId,
        int clientId,
        int? clientPortalAccountId,
        string channel,
        string provider,
        string displayName,
        string address,
        string verificationMethod,
        CancellationToken ct)
    {
        EnsureEnabled();
        var normalizedChannel = NormalizeChannel(channel);
        var normalizedAddress = NormalizeRequired(address, 256, "Укажите контакт получателя.");
        var normalizedProvider = NormalizeRequired(provider, 64, "Укажите провайдера.");
        var normalizedName = NormalizeRequired(displayName, 256, "Укажите имя получателя.");
        var normalizedMethod = NormalizeRequired(verificationMethod, 64, "Укажите способ подтверждения контакта.");

        if (!await _db.Clients.AsNoTracking().AnyAsync(item => item.Id == clientId && item.IsActive, ct))
        {
            throw new InvalidOperationException("Активный клиент не найден.");
        }

        if (clientPortalAccountId.HasValue
            && !await _db.ClientPortalAccounts.AsNoTracking().AnyAsync(
                item => item.Id == clientPortalAccountId.Value && item.ClientId == clientId && item.IsActive,
                ct))
        {
            throw new InvalidOperationException("Представитель не принадлежит выбранному клиенту или отключён.");
        }

        var addressHash = Hash($"{normalizedChannel}|{normalizedAddress.ToUpperInvariant()}");
        var existing = await _db.MessengerRecipientBindings.FirstOrDefaultAsync(
            item => item.ClientId == clientId
                && item.Channel == normalizedChannel
                && item.AddressHash == addressHash,
            ct);
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new MessengerRecipientBinding
            {
                ClientId = clientId,
                ClientPortalAccountId = clientPortalAccountId,
                Channel = normalizedChannel,
                Provider = normalizedProvider,
                AddressHash = addressHash,
                CreatedAt = now,
            };
            _db.MessengerRecipientBindings.Add(existing);
        }

        existing.DisplayName = normalizedName;
        existing.ProtectedAddress = _addressProtector.Protect(normalizedAddress);
        existing.MaskedAddress = MaskAddress(normalizedAddress);
        existing.Provider = normalizedProvider;
        existing.VerificationStatus = "verified";
        existing.VerificationMethod = normalizedMethod;
        existing.VerifiedAt = now;
        existing.VerifiedByUserId = userId;
        existing.IsActive = true;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<PreparedMessengerDocumentDelivery> PrepareCaseDocumentAsync(
        int userId,
        int documentId,
        int recipientBindingId,
        string? message,
        CancellationToken ct)
    {
        EnsureEnabled();
        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Include(item => item.ClientCase)
            .FirstOrDefaultAsync(item => item.Id == documentId, ct)
            ?? throw new InvalidOperationException("Документ дела не найден.");
        var binding = await _db.MessengerRecipientBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == recipientBindingId && item.IsActive, ct)
            ?? throw new InvalidOperationException("Контакт получателя не найден.");

        if (!string.Equals(binding.VerificationStatus, "verified", StringComparison.Ordinal)
            || binding.VerifiedAt is null)
        {
            throw new InvalidOperationException("Контакт получателя не подтверждён.");
        }
        if (!document.ClientCase.ClientId.HasValue || binding.ClientId != document.ClientCase.ClientId)
        {
            throw new InvalidOperationException("Получатель не принадлежит клиенту выбранного дела.");
        }
        if (!_caseDocumentStorage.Exists(document.StoragePath))
        {
            throw new InvalidOperationException("Файл документа отсутствует в хранилище.");
        }
        if (document.SizeBytes <= 0 || document.SizeBytes > MaximumSnapshotBytes)
        {
            throw new InvalidOperationException("Размер документа недопустим для отправки.");
        }

        var publicId = Guid.NewGuid();
        var snapshotRelativePath = Path.Combine(
            DateTime.UtcNow.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            DateTime.UtcNow.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
            $"{publicId:N}.snapshot");
        var snapshotFullPath = ResolveSnapshotPath(snapshotRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotFullPath)!);

        string checksum;
        long size;
        await using (var source = new FileStream(
            _caseDocumentStorage.ResolvePath(document.StoragePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(
            snapshotFullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                size += read;
                if (size > MaximumSnapshotBytes)
                {
                    throw new InvalidOperationException("Документ превышает допустимый размер.");
                }
                incrementalHash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            checksum = Convert.ToHexString(incrementalHash.GetHashAndReset());
        }

        var idempotencyKey = Hash($"case-document|{document.Id}|{document.Version}|{checksum}|{binding.Id}");
        var existing = await _db.MessengerDocumentDeliveries
            .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey && item.Status != MessengerDocumentDeliveryStatuses.Cancelled, ct);
        if (existing is not null)
        {
            File.Delete(snapshotFullPath);
            var renewedToken = GenerateToken();
            RenewPreparedDelivery(existing, renewedToken, message);
            await _db.SaveChangesAsync(ct);
            return new PreparedMessengerDocumentDelivery(existing, renewedToken);
        }

        var token = GenerateToken();
        var now = DateTime.UtcNow;
        var delivery = new MessengerDocumentDelivery
        {
            PublicId = publicId,
            RecipientBindingId = binding.Id,
            ClientId = binding.ClientId,
            ClientCaseId = document.ClientCaseId,
            DocumentType = "case-document",
            DocumentId = document.Id,
            DocumentVersion = document.Version,
            OriginalFileName = document.OriginalFileName,
            SnapshotPath = snapshotRelativePath,
            ContentType = document.ContentType,
            SizeBytes = size,
            ChecksumSha256 = checksum,
            Channel = binding.Channel,
            Provider = binding.Provider,
            IdempotencyKey = idempotencyKey,
            LinkTokenHash = Hash(token),
            ProtectedLinkToken = _addressProtector.Protect(token),
            LinkExpiresAt = now.Add(_linkLifetime),
            MessagePreview = NormalizeOptional(message, 1000),
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MessengerDocumentDeliveries.Add(delivery);
        await _db.SaveChangesAsync(ct);
        return new PreparedMessengerDocumentDelivery(delivery, token);
    }

    public async Task<MessengerDocumentDelivery> QueueAsync(int deliveryId, CancellationToken ct)
    {
        EnsureEnabled();
        var delivery = await _db.MessengerDocumentDeliveries
            .Include(item => item.RecipientBinding)
            .FirstOrDefaultAsync(item => item.Id == deliveryId, ct)
            ?? throw new InvalidOperationException("Подготовленная отправка не найдена.");
        if (!delivery.RecipientBinding.IsActive
            || !string.Equals(delivery.RecipientBinding.VerificationStatus, "verified", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Контакт получателя отключён или больше не подтверждён.");
        }
        if (delivery.DownloadCount > 0 || delivery.LinkExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Одноразовая ссылка уже использована или истекла. Подготовьте отправку повторно.");
        }
        if (string.IsNullOrWhiteSpace(delivery.ProtectedLinkToken))
        {
            throw new InvalidOperationException("Защищённый токен отправки отсутствует. Подготовьте отправку повторно.");
        }

        var now = DateTime.UtcNow;
        delivery.Status = MessengerDocumentDeliveryStatuses.Queued;
        delivery.QueuedAt = now;
        delivery.NextAttemptAt = now;
        delivery.LastError = null;
        delivery.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return delivery;
    }

    public async Task<PreparedMessengerDocumentDelivery> PrepareGeneratedDocumentAsync(
        int userId,
        string documentType,
        int documentId,
        int documentVersion,
        int clientId,
        string originalFileName,
        string contentType,
        Stream source,
        long declaredSize,
        int recipientBindingId,
        string? message,
        CancellationToken ct)
    {
        EnsureEnabled();
        var normalizedType = documentType.Trim().ToLowerInvariant();
        if (normalizedType is not ("invoice" or "act" or "contract"))
        {
            throw new InvalidOperationException("Тип финансового документа не поддерживается.");
        }
        if (documentId <= 0 || documentVersion <= 0 || clientId <= 0)
        {
            throw new InvalidOperationException("Источник документа указан неверно.");
        }
        if (!string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Для отправки финансовых документов разрешён только PDF.");
        }
        if (declaredSize <= 0 || declaredSize > MaximumSnapshotBytes)
        {
            throw new InvalidOperationException("Размер документа недопустим для отправки.");
        }

        var binding = await _db.MessengerRecipientBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == recipientBindingId && item.IsActive, ct)
            ?? throw new InvalidOperationException("Контакт получателя не найден.");
        if (binding.ClientId != clientId
            || binding.VerifiedAt is null
            || !string.Equals(binding.VerificationStatus, "verified", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Получатель не подтверждён или не принадлежит выбранному клиенту.");
        }

        var publicId = Guid.NewGuid();
        var snapshotRelativePath = Path.Combine(
            DateTime.UtcNow.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            DateTime.UtcNow.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
            $"{publicId:N}.snapshot");
        var snapshotFullPath = ResolveSnapshotPath(snapshotRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotFullPath)!);

        string checksum;
        long size = 0;
        try
        {
            await using var target = new FileStream(
                snapshotFullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (size == 0
                    && (read < 5
                        || buffer[0] != (byte)'%'
                        || buffer[1] != (byte)'P'
                        || buffer[2] != (byte)'D'
                        || buffer[3] != (byte)'F'
                        || buffer[4] != (byte)'-'))
                {
                    throw new InvalidOperationException("Сформированный файл не является PDF.");
                }
                size += read;
                if (size > MaximumSnapshotBytes)
                {
                    throw new InvalidOperationException("Документ превышает допустимый размер.");
                }
                incrementalHash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            checksum = Convert.ToHexString(incrementalHash.GetHashAndReset());
        }
        catch
        {
            File.Delete(snapshotFullPath);
            throw;
        }
        if (size != declaredSize)
        {
            File.Delete(snapshotFullPath);
            throw new InvalidOperationException("Размер сформированного документа изменился при передаче.");
        }

        var idempotencyKey = Hash($"{normalizedType}|{documentId}|{documentVersion}|{checksum}|{binding.Id}");
        var existing = await _db.MessengerDocumentDeliveries.FirstOrDefaultAsync(
            item => item.IdempotencyKey == idempotencyKey && item.Status != MessengerDocumentDeliveryStatuses.Cancelled,
            ct);
        if (existing is not null)
        {
            File.Delete(snapshotFullPath);
            var renewedToken = GenerateToken();
            RenewPreparedDelivery(existing, renewedToken, message);
            await _db.SaveChangesAsync(ct);
            return new(existing, renewedToken);
        }

        var token = GenerateToken();
        var now = DateTime.UtcNow;
        var delivery = new MessengerDocumentDelivery
        {
            PublicId = publicId,
            RecipientBindingId = binding.Id,
            ClientId = binding.ClientId,
            DocumentType = normalizedType,
            DocumentId = documentId,
            DocumentVersion = documentVersion,
            OriginalFileName = NormalizeRequired(originalFileName, 260, "Имя документа не указано."),
            SnapshotPath = snapshotRelativePath,
            ContentType = "application/pdf",
            SizeBytes = size,
            ChecksumSha256 = checksum,
            Channel = binding.Channel,
            Provider = binding.Provider,
            IdempotencyKey = idempotencyKey,
            LinkTokenHash = Hash(token),
            ProtectedLinkToken = _addressProtector.Protect(token),
            LinkExpiresAt = now.Add(_linkLifetime),
            MessagePreview = NormalizeOptional(message, 1000),
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MessengerDocumentDeliveries.Add(delivery);
        await _db.SaveChangesAsync(ct);
        return new(delivery, token);
    }

    public async Task<MessengerDocumentDownload?> ConsumeDownloadAsync(
        Guid publicId,
        string token,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedToken = token.Trim();
        if (!_enabled || normalizedToken.Length is 0 or > 512)
        {
            return null;
        }

        var delivery = await _db.MessengerDocumentDeliveries
            .AsNoTracking()
            .Where(item => item.PublicId == publicId)
            .Select(item => new DownloadSnapshot(
                item.Id,
                item.CancelledAt,
                item.LinkExpiresAt,
                item.DownloadCount,
                item.LinkTokenHash,
                item.SnapshotPath,
                item.ContentType,
                item.OriginalFileName,
                item.ChecksumSha256))
            .FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        var tokenHash = Hash(normalizedToken);
        if (delivery is null
            || delivery.CancelledAt.HasValue
            || delivery.LinkExpiresAt is null
            || delivery.LinkExpiresAt <= now
            || delivery.DownloadCount > 0
            || string.IsNullOrWhiteSpace(delivery.LinkTokenHash)
            || !FixedTimeEquals(delivery.LinkTokenHash, tokenHash))
        {
            return null;
        }

        var path = ResolveSnapshotPath(delivery.SnapshotPath);
        if (!File.Exists(path))
        {
            await _db.MessengerDocumentDeliveries
                .Where(item => item.Id == delivery.Id && item.DownloadCount == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, MessengerDocumentDeliveryStatuses.Failed)
                        .SetProperty(item => item.FailedAt, now)
                        .SetProperty(item => item.LastError, "SnapshotMissing")
                        .SetProperty(item => item.UpdatedAt, now),
                    ct);
            return null;
        }

        // Второй запрос повторяет все условия доступа и атомарно забирает одноразовую
        // ссылку. Поэтому два параллельных HTTP-запроса не могут скачать один снимок.
        var consumed = await _db.MessengerDocumentDeliveries
            .Where(item => item.Id == delivery.Id
                && item.CancelledAt == null
                && item.LinkExpiresAt != null
                && item.LinkExpiresAt > now
                && item.DownloadCount == 0
                && item.LinkTokenHash == tokenHash)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.DownloadCount, 1)
                    .SetProperty(item => item.FirstOpenedAt, item => item.FirstOpenedAt ?? now)
                    .SetProperty(item => item.LastOpenedAt, now)
                    .SetProperty(item => item.ReadAt, item => item.ReadAt ?? now)
                    .SetProperty(item => item.Status, MessengerDocumentDeliveryStatuses.Read)
                    .SetProperty(item => item.UpdatedAt, now),
                ct);
        if (consumed != 1)
        {
            return null;
        }

        return new MessengerDocumentDownload(path, delivery.ContentType, delivery.OriginalFileName, delivery.ChecksumSha256);
    }

    /// <summary>Минимальный неизменяемый срез одноразовой ссылки без загрузки связей доставки.</summary>
    private sealed record DownloadSnapshot(
        int Id,
        DateTime? CancelledAt,
        DateTime? LinkExpiresAt,
        int DownloadCount,
        string? LinkTokenHash,
        string SnapshotPath,
        string ContentType,
        string OriginalFileName,
        string ChecksumSha256);

    private string ResolveSnapshotPath(string relativePath)
    {
        var root = Path.GetFullPath(_snapshotRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Некорректный путь снимка документа.");
        }
        return fullPath;
    }

    /// <summary>
    /// Начинает новый цикл отправки существующего неизменяемого снимка и удаляет
    /// все отметки предыдущего цикла, чтобы история не считала новую ссылку прочитанной.
    /// </summary>
    private void RenewPreparedDelivery(MessengerDocumentDelivery delivery, string token, string? message)
    {
        var renewedAt = DateTime.UtcNow;
        delivery.LinkTokenHash = Hash(token);
        delivery.ProtectedLinkToken = _addressProtector.Protect(token);
        delivery.LinkExpiresAt = renewedAt.Add(_linkLifetime);
        delivery.Status = MessengerDocumentDeliveryStatuses.Prepared;
        delivery.ProviderMessageId = null;
        delivery.DownloadCount = 0;
        delivery.AttemptCount = 0;
        delivery.NextAttemptAt = null;
        delivery.FirstOpenedAt = null;
        delivery.LastOpenedAt = null;
        delivery.QueuedAt = null;
        delivery.SentAt = null;
        delivery.DeliveredAt = null;
        delivery.ReadAt = null;
        delivery.FailedAt = null;
        delivery.MessagePreview = NormalizeOptional(message, 1000);
        delivery.LastError = null;
        delivery.UpdatedAt = renewedAt;
    }

    private void EnsureEnabled()
    {
        if (!_enabled)
        {
            throw new InvalidOperationException("Отправка документов в мессенджеры отключена администратором.");
        }
    }

    private static string NormalizeChannel(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return SupportedChannels.Contains(normalized)
            ? normalized
            : throw new InvalidOperationException("Канал доставки не поддерживается.");
    }

    private static string NormalizeRequired(string value, int maxLength, string error)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(error);
        }
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string MaskAddress(string address)
    {
        if (address.Length <= 4)
        {
            return new string('*', address.Length);
        }
        return $"{address[..2]}{new string('*', Math.Min(8, address.Length - 4))}{address[^2..]}";
    }

    private static string GenerateToken()
        => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record PreparedMessengerDocumentDelivery(MessengerDocumentDelivery Delivery, string? Token);
public sealed record MessengerDocumentDownload(string FullPath, string ContentType, string FileName, string ChecksumSha256);
