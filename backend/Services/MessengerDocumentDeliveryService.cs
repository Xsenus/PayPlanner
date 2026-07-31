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
            var renewedAt = DateTime.UtcNow;
            existing.LinkTokenHash = Hash(renewedToken);
            existing.ProtectedLinkToken = _addressProtector.Protect(renewedToken);
            existing.LinkExpiresAt = renewedAt.Add(_linkLifetime);
            existing.DownloadCount = 0;
            existing.Status = MessengerDocumentDeliveryStatuses.Prepared;
            existing.LastError = null;
            existing.UpdatedAt = renewedAt;
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

    public async Task<MessengerDocumentDownload?> ConsumeDownloadAsync(
        Guid publicId,
        string token,
        CancellationToken ct)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var delivery = await _db.MessengerDocumentDeliveries
            .FirstOrDefaultAsync(item => item.PublicId == publicId, ct);
        if (delivery is null
            || delivery.CancelledAt.HasValue
            || delivery.LinkExpiresAt is null
            || delivery.LinkExpiresAt <= DateTime.UtcNow
            || delivery.DownloadCount > 0
            || string.IsNullOrWhiteSpace(delivery.LinkTokenHash)
            || !FixedTimeEquals(delivery.LinkTokenHash, Hash(token.Trim())))
        {
            return null;
        }

        var path = ResolveSnapshotPath(delivery.SnapshotPath);
        if (!File.Exists(path))
        {
            delivery.Status = MessengerDocumentDeliveryStatuses.Failed;
            delivery.FailedAt = DateTime.UtcNow;
            delivery.LastError = "SnapshotMissing";
            delivery.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return null;
        }

        var now = DateTime.UtcNow;
        delivery.DownloadCount = 1;
        delivery.FirstOpenedAt ??= now;
        delivery.LastOpenedAt = now;
        delivery.ReadAt ??= now;
        delivery.Status = MessengerDocumentDeliveryStatuses.Read;
        delivery.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return new MessengerDocumentDownload(path, delivery.ContentType, delivery.OriginalFileName, delivery.ChecksumSha256);
    }

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
