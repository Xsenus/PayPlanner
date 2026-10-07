using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

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
    private readonly IProductStorageReader? _productStorageReader;
    private readonly ILogger<MessengerDocumentDeliveryService> _logger;
    private readonly IStorageObjectService? _managedStorage;
    private readonly IProductStorageBindingService? _storagePublisher;
    private readonly IOptionsMonitor<StorageOrchestrationOptions>? _storageOptions;
    private readonly IStorageStageCapacityService? _stageCapacity;

    public MessengerDocumentDeliveryService(
        PaymentContext db,
        ICaseDocumentStorage caseDocumentStorage,
        IDataProtectionProvider dataProtectionProvider,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IProductStorageReader? productStorageReader = null,
        ILogger<MessengerDocumentDeliveryService>? logger = null,
        IStorageObjectService? managedStorage = null,
        IProductStorageBindingService? storagePublisher = null,
        IOptionsMonitor<StorageOrchestrationOptions>? storageOptions = null,
        IStorageStageCapacityService? stageCapacity = null)
    {
        _db = db;
        _productStorageReader = productStorageReader;
        _logger = logger ?? NullLogger<MessengerDocumentDeliveryService>.Instance;
        _managedStorage = managedStorage;
        _storagePublisher = storagePublisher;
        _storageOptions = storageOptions;
        _stageCapacity = stageCapacity;
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
        var snapshotPolicy = CaptureSnapshotPolicy();
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

        string checksum = string.Empty;
        long size = 0;
        var snapshotCreated = false;
        StorageStageCapacityTicket? stageTicket = null;
        var stageSettled = false;
        StorageStageFileReceipt? initialCheckpoint = null, completedCheckpoint = null;
        SnapshotCleanupProof? legacyProof = null;
        try
        {
            await using var source = new FileStream(
                _caseDocumentStorage.ResolvePath(document.StoragePath),
                FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var expectedSize = source.Length;
            if (expectedSize <= 0 || expectedSize > MaximumSnapshotBytes)
                throw new InvalidOperationException("Размер исходного файла недопустим для отправки.");
            RequireSnapshotPolicy(snapshotPolicy);
            stageTicket = await ReserveSnapshotStageAsync(snapshotFullPath, expectedSize, ct);
            async Task WriteSnapshotAsync(Stream target, CancellationToken token)
            {
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                size = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    RequireSnapshotPolicy(snapshotPolicy);
                    size += read;
                    if (size > MaximumSnapshotBytes || (stageTicket is not null && size > stageTicket.SizeBytes))
                    {
                        throw new InvalidOperationException("Документ превышает допустимый размер.");
                    }
                    incrementalHash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                }
                RequireSnapshotPolicy(snapshotPolicy);
                checksum = Convert.ToHexString(incrementalHash.GetHashAndReset());
            }
            if (stageTicket is not null)
            {
                completedCheckpoint = await _stageCapacity!.WriteCheckpointedFileAsync(stageTicket, async (output, initial, token) =>
                {
                    snapshotCreated = true;
                    initialCheckpoint = initial;
                    await WriteSnapshotAsync(output, token);
                }, ct);
            }
            else
            {
                var legacyRootIdentity = StorageStageRootIdentity.Observe(Path.GetDirectoryName(_snapshotRoot)!);
                await using var target = new FileStream(snapshotFullPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                snapshotCreated = true;
                var originalIdentity = StorageStageFileIdentity.Observe(target.SafeFileHandle);
                try
                {
                    await WriteSnapshotAsync(target, ct);
                    legacyProof = await CaptureSnapshotCleanupProofAsync(target, legacyRootIdentity, originalIdentity, ct);
                    if (legacyProof is null || legacyProof.Bytes != size
                        || !string.Equals(legacyProof.Sha256, checksum, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Не удалось подтвердить временную копию документа.");
                }
                catch
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { legacyProof = await CaptureSnapshotCleanupProofAsync(target, legacyRootIdentity, originalIdentity, cleanup.Token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                    { _logger.LogWarning(error, "Could not verify failed messenger snapshot {Path}", snapshotFullPath); }
                    throw;
                }
            }
            if (stageTicket is not null)
            {
                stageTicket = await _stageCapacity!.ConfirmPhysicalSizeAsync(stageTicket, size, ct);
                await _stageCapacity.SettlePhysicalAsync(stageTicket, ct);
                stageSettled = true;
            }
        }
        catch
        {
            if (snapshotCreated)
            {
                await TryDeletePreparedSnapshotAsync(snapshotFullPath, stageTicket, completedCheckpoint ?? initialCheckpoint, legacyProof);
            }
            throw;
        }
        finally
        {
            if (stageTicket is not null && !stageSettled && !File.Exists(snapshotFullPath))
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _stageCapacity!.ReleaseAsync(stageTicket, cleanup.Token);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not release messenger snapshot stage capacity {ReservationKey}", stageTicket.Key);
                }
            }
            if (stageTicket is not null) await stageTicket.CloseWriterAsync();
        }

        var idempotencyKey = Hash($"case-document|{document.Id}|{document.Version}|{checksum}|{binding.Id}");
        var existing = await _db.MessengerDocumentDeliveries
            .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey && item.Status != MessengerDocumentDeliveryStatuses.Cancelled, ct);
        if (existing is not null)
        {
            await TryDeletePreparedSnapshotAsync(snapshotFullPath, stageTicket, completedCheckpoint ?? initialCheckpoint, legacyProof);
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
        return await PersistPreparedSnapshotAsync(delivery, token, snapshotFullPath, snapshotPolicy,
            GetSnapshotCleanupProof(stageTicket, completedCheckpoint ?? initialCheckpoint, legacyProof), ct);
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
        var snapshotPolicy = CaptureSnapshotPolicy();
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

        string checksum = string.Empty;
        long size = 0;
        var snapshotCreated = false;
        StorageStageCapacityTicket? stageTicket = null;
        var stageSettled = false;
        StorageStageFileReceipt? initialCheckpoint = null, completedCheckpoint = null;
        SnapshotCleanupProof? legacyProof = null;
        try
        {
            RequireSnapshotPolicy(snapshotPolicy);
            stageTicket = await ReserveSnapshotStageAsync(snapshotFullPath, declaredSize, ct);
            async Task WriteSnapshotAsync(Stream target, CancellationToken token)
            {
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    RequireSnapshotPolicy(snapshotPolicy);
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
                    if (size > MaximumSnapshotBytes || (stageTicket is not null && size > stageTicket.SizeBytes))
                    {
                        throw new InvalidOperationException("Документ превышает допустимый размер.");
                    }
                    incrementalHash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                }
                RequireSnapshotPolicy(snapshotPolicy);
                checksum = Convert.ToHexString(incrementalHash.GetHashAndReset());
            }
            if (stageTicket is not null)
            {
                completedCheckpoint = await _stageCapacity!.WriteCheckpointedFileAsync(stageTicket, async (output, initial, token) =>
                {
                    snapshotCreated = true;
                    initialCheckpoint = initial;
                    await WriteSnapshotAsync(output, token);
                }, ct);
            }
            else
            {
                var legacyRootIdentity = StorageStageRootIdentity.Observe(Path.GetDirectoryName(_snapshotRoot)!);
                await using var target = new FileStream(snapshotFullPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                snapshotCreated = true;
                var originalIdentity = StorageStageFileIdentity.Observe(target.SafeFileHandle);
                try
                {
                    await WriteSnapshotAsync(target, ct);
                    legacyProof = await CaptureSnapshotCleanupProofAsync(target, legacyRootIdentity, originalIdentity, ct);
                    if (legacyProof is null || legacyProof.Bytes != size
                        || !string.Equals(legacyProof.Sha256, checksum, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Не удалось подтвердить временную копию документа.");
                }
                catch
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { legacyProof = await CaptureSnapshotCleanupProofAsync(target, legacyRootIdentity, originalIdentity, cleanup.Token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                    { _logger.LogWarning(error, "Could not verify failed messenger snapshot {Path}", snapshotFullPath); }
                    throw;
                }
            }
            if (stageTicket is not null)
            {
                stageTicket = await _stageCapacity!.ConfirmPhysicalSizeAsync(stageTicket, size, ct);
                await _stageCapacity.SettlePhysicalAsync(stageTicket, ct);
                stageSettled = true;
            }
        }
        catch
        {
            if (snapshotCreated)
            {
                await TryDeletePreparedSnapshotAsync(snapshotFullPath, stageTicket, completedCheckpoint ?? initialCheckpoint, legacyProof);
            }
            throw;
        }
        finally
        {
            if (stageTicket is not null && !stageSettled && !File.Exists(snapshotFullPath))
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _stageCapacity!.ReleaseAsync(stageTicket, cleanup.Token);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not release messenger snapshot stage capacity {ReservationKey}", stageTicket.Key);
                }
            }
            if (stageTicket is not null) await stageTicket.CloseWriterAsync();
        }
        if (size != declaredSize)
        {
            await TryDeletePreparedSnapshotAsync(snapshotFullPath, stageTicket, completedCheckpoint ?? initialCheckpoint, legacyProof);
            throw new InvalidOperationException("Размер сформированного документа изменился при передаче.");
        }

        var idempotencyKey = Hash($"{normalizedType}|{documentId}|{documentVersion}|{checksum}|{binding.Id}");
        var existing = await _db.MessengerDocumentDeliveries.FirstOrDefaultAsync(
            item => item.IdempotencyKey == idempotencyKey && item.Status != MessengerDocumentDeliveryStatuses.Cancelled,
            ct);
        if (existing is not null)
        {
            await TryDeletePreparedSnapshotAsync(snapshotFullPath, stageTicket, completedCheckpoint ?? initialCheckpoint, legacyProof);
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
        return await PersistPreparedSnapshotAsync(delivery, token, snapshotFullPath, snapshotPolicy,
            GetSnapshotCleanupProof(stageTicket, completedCheckpoint ?? initialCheckpoint, legacyProof), ct);
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
                item.ChecksumSha256,
                item.SizeBytes))
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

        if (_productStorageReader is not null)
        {
            var managed = await _productStorageReader.OpenReadAsync(LegacyStorageSources.MessengerSnapshots,
                delivery.Id, delivery.OriginalFileName, delivery.ContentType, ct);
            if (managed.Status != ProductStorageReadStatus.Legacy)
            {
                if (managed.Status != ProductStorageReadStatus.Available || managed.File is not { } file)
                    return null;
                var transferred = false;
                try
                {
                    if (file.SizeBytes != delivery.SizeBytes || string.IsNullOrWhiteSpace(file.Sha256)
                        || !string.Equals(file.Sha256, delivery.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (!await TryConsumeLinkAsync(delivery, tokenHash, ct)) return null;
                    transferred = true;
                    return new MessengerDocumentDownload(string.Empty, delivery.ContentType,
                        delivery.OriginalFileName, delivery.ChecksumSha256, file.Content);
                }
                finally
                {
                    if (!transferred) await file.DisposeAsync();
                }
            }
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

        if (!await TryConsumeLinkAsync(delivery, tokenHash, ct)) return null;
        return new MessengerDocumentDownload(path, delivery.ContentType, delivery.OriginalFileName, delivery.ChecksumSha256);
    }

    private async Task<bool> TryConsumeLinkAsync(DownloadSnapshot delivery, string tokenHash, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
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
        return consumed == 1;
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
        string ChecksumSha256,
        long SizeBytes);

    private async Task<PreparedMessengerDocumentDelivery> PersistPreparedSnapshotAsync(
        MessengerDocumentDelivery delivery, string token, string snapshotFullPath, (bool Managed, bool Capacity) snapshotPolicy,
        SnapshotCleanupProof? cleanupProof, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        var commitStarted = false;
        var snapshotWriteKey = $"messenger-snapshot:{delivery.IdempotencyKey}:{delivery.CreatedByUserId}";
        try
        {
            RequireSnapshotPolicy(snapshotPolicy);
            StorageObjectWriteResult? managed = null;
            var pendingOwner = $"upload:messenger:{delivery.IdempotencyKey}:{delivery.CreatedByUserId}";
            if (ManagedSnapshotWritesEnabled())
            {
                if (_managedStorage is null || _storagePublisher is null)
                    throw new InvalidOperationException("Управляемое хранилище документов не настроено.");
                if (_storageOptions?.CurrentValue.UploadCapacityChecksEnabled != true)
                    throw new InvalidOperationException("Для управляемой отправки документов включите долговечное резервирование места загрузок.");
                // The immutable content key survives a failed preparation. Retry
                // recovers the same durable operation rather than another PUT.
                // Provider I/O finishes before the short domain transaction starts.
                await using var source = new FileStream(snapshotFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                managed = await _managedStorage.WriteAsync(new("messenger-delivery", "messenger-delivery", pendingOwner,
                    Path.GetFileName(delivery.OriginalFileName), delivery.ContentType, source,
                    snapshotWriteKey, delivery.CreatedByUserId,
                    ExpectedSizeBytes: delivery.SizeBytes, ExpectedSha256: delivery.ChecksumSha256), ct);
                if (managed.ObjectId == Guid.Empty || managed.Status != "available")
                    throw new InvalidOperationException("Хранилище ещё не подтвердило сохранение документа. Повторите подготовку после восстановления хранилища.");
            }
            RequireSnapshotPolicy(snapshotPolicy);
            transaction = await _db.Database.BeginTransactionAsync(ct);
            _db.MessengerDocumentDeliveries.Add(delivery);
            await _db.SaveChangesAsync(ct);
            if (managed is not null)
            {
                if (!ManagedSnapshotWritesEnabled())
                    throw new InvalidOperationException("Режим хранения изменился во время подготовки документа.");
                var checksum = delivery.ChecksumSha256.ToLowerInvariant();
                var name = Path.GetFileName(delivery.OriginalFileName);
                var owner = delivery.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var changed = await _db.StorageObjects.Where(item => item.Id == managed.ObjectId && !item.IsDeleted
                        && item.DataClass == "messenger-delivery" && item.OwnerType == "messenger-delivery"
                        && item.OwnerId == pendingOwner && item.LogicalName == name && item.ContentType == delivery.ContentType
                        && item.Version < long.MaxValue && item.CurrentGeneration == managed.Generation
                        && _db.StorageObjectGenerations.Any(generation => generation.StorageObjectId == item.Id
                            && generation.Generation == managed.Generation && generation.SizeBytes == delivery.SizeBytes
                            && generation.Sha256 == checksum))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.OwnerId, owner)
                        .SetProperty(item => item.Version, item => item.Version + 1), ct);
                if (changed != 1)
                    throw new InvalidOperationException("Не удалось подтвердить версию документа в хранилище.");
                var published = await _storagePublisher!.PublishAsync(new(LegacyStorageSources.MessengerSnapshots,
                    delivery.Id, managed.ObjectId, name, delivery.ContentType, 0), ct);
                if (!published.Published)
                    throw new InvalidOperationException("Обязательные копии документа ещё не подтверждены.");
            }
            RequireSnapshotPolicy(snapshotPolicy);
            commitStarted = true;
            await transaction.CommitAsync(ct);
            return new(delivery, token);
        }
        catch (Exception failure)
        {
            // An error after commit starts may be a lost acknowledgement.
            // Keep bytes that might already have a durable database link.
            if (!commitStarted)
            {
                var rollbackConfirmed = transaction is null;
                if (transaction is not null)
                {
                    try
                    {
                        using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await transaction.RollbackAsync(rollbackTimeout.Token);
                        rollbackConfirmed = true;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Could not confirm rollback of messenger snapshot registration");
                    }
                }
                if (rollbackConfirmed) await TryDeleteSnapshotCleanupProofAsync(snapshotFullPath, cleanupProof);
                _db.Entry(delivery).State = EntityState.Detached;
                if (rollbackConfirmed && (failure is DbUpdateException update && IsSnapshotKeyCollision(update)
                    || StorageObjectService.IsReplayOwnerMismatch(failure, snapshotWriteKey)))
                {
                    // PostgreSQL cannot query an aborted transaction. Dispose it
                    // before reading the winner, without masking the collision.
                    var disposed = transaction is null;
                    if (transaction is not null)
                    {
                        var completed = transaction;
                        transaction = null;
                        try
                        {
                            await completed.DisposeAsync();
                            disposed = true;
                        }
                        catch (Exception disposalError)
                        {
                            _logger.LogWarning(disposalError, "Could not close failed messenger snapshot transaction");
                        }
                    }
                    if (disposed)
                    {
                        var winner = await ReadConcurrentPreparedSnapshotAsync(delivery, snapshotPolicy, ct);
                        if (winner is not null) return winner;
                    }
                }
            }
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private static bool IsSnapshotKeyCollision(DbUpdateException exception)
        => exception.InnerException is Npgsql.PostgresException
        { SqlState: "23505", ConstraintName: "IX_MessengerDocumentDeliveries_IdempotencyKey" }
            || exception.InnerException is Microsoft.Data.Sqlite.SqliteException
            { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 } sqlite
                && sqlite.Message.Contains("UNIQUE constraint failed: MessengerDocumentDeliveries.IdempotencyKey", StringComparison.Ordinal);

    private async Task<PreparedMessengerDocumentDelivery?> ReadConcurrentPreparedSnapshotAsync(
        MessengerDocumentDelivery attempted, (bool Managed, bool Capacity) policy, CancellationToken ct)
    {
        RequireSnapshotPolicy(policy);
        var winner = await _db.MessengerDocumentDeliveries.AsNoTracking()
            .Include(item => item.RecipientBinding)
            .SingleOrDefaultAsync(item => item.IdempotencyKey == attempted.IdempotencyKey, ct);
        if (winner is null || winner.Status != MessengerDocumentDeliveryStatuses.Prepared
            || winner.DownloadCount != 0 || winner.LinkExpiresAt is null || winner.LinkExpiresAt <= DateTime.UtcNow
            || !winner.RecipientBinding.IsActive || winner.RecipientBinding.VerifiedAt is null
            || winner.RecipientBinding.VerificationStatus != "verified"
            || winner.RecipientBinding.ClientId != winner.ClientId
            || winner.RecipientBinding.Channel != winner.Channel || winner.RecipientBinding.Provider != winner.Provider
            || winner.RecipientBindingId != attempted.RecipientBindingId || winner.ClientId != attempted.ClientId
            || winner.ClientCaseId != attempted.ClientCaseId || winner.DocumentType != attempted.DocumentType
            || winner.DocumentId != attempted.DocumentId || winner.DocumentVersion != attempted.DocumentVersion
            || winner.OriginalFileName != attempted.OriginalFileName || winner.ContentType != attempted.ContentType
            || winner.SizeBytes != attempted.SizeBytes
            || !string.Equals(winner.ChecksumSha256, attempted.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            || winner.Channel != attempted.Channel || winner.Provider != attempted.Provider
            || winner.CreatedByUserId != attempted.CreatedByUserId || winner.MessagePreview != attempted.MessagePreview
            || string.IsNullOrWhiteSpace(winner.ProtectedLinkToken))
            return null;
        string token;
        try { token = _addressProtector.Unprotect(winner.ProtectedLinkToken); }
        catch (CryptographicException) { return null; }
        if (Hash(token) != winner.LinkTokenHash) return null;
        if (policy.Managed)
        {
            if (_storagePublisher is not IProductStoragePublishedReceiptVerifier verifier
                || !await verifier.VerifyPublishedReceiptAsync(new(LegacyStorageSources.MessengerSnapshots, winner.Id,
                    winner.OriginalFileName, winner.ContentType, winner.SizeBytes, winner.ChecksumSha256,
                    winner.CreatedByUserId, $"messenger-snapshot:{winner.IdempotencyKey}:{winner.CreatedByUserId}"), ct))
                return null;
        }
        RequireSnapshotPolicy(policy);
        return new(winner, token);
    }

    private (bool Managed, bool Capacity) CaptureSnapshotPolicy()
    {
        var configured = _storageOptions?.CurrentValue;
        return (configured is { Enabled: true } && configured.ProductWriteEnabledClasses.Contains("messenger-delivery", StringComparer.Ordinal),
            configured?.UploadCapacityChecksEnabled == true);
    }

    private void RequireSnapshotPolicy((bool Managed, bool Capacity) expected)
    {
        if (CaptureSnapshotPolicy() != expected)
            throw new InvalidOperationException("Настройки хранения изменились во время подготовки документа. Повторите подготовку.");
    }

    private async Task<StorageStageCapacityTicket?> ReserveSnapshotStageAsync(string path, long size, CancellationToken ct)
    {
        if (ManagedSnapshotWritesEnabled()
            && (_managedStorage is null || _storagePublisher is null
                || _storageOptions?.CurrentValue.UploadCapacityChecksEnabled != true))
            throw new InvalidOperationException("Для управляемой отправки документов настройте хранилище и учёт места загрузок.");
        if (_storageOptions?.CurrentValue.UploadCapacityChecksEnabled != true) return null;
        if (_stageCapacity is null)
            throw new InvalidOperationException("Учёт места для подготовки документа не настроен.");
        var admission = await _stageCapacity.ReserveCompatibilityAsync(
            "messenger-snapshot:" + Path.GetFileName(path), path, size, "files", ct);
        return admission.Ticket ?? throw new InvalidOperationException("Недостаточно подтверждённого места для подготовки документа.");
    }

    private bool ManagedSnapshotWritesEnabled()
        => _storageOptions?.CurrentValue is { Enabled: true } options
            && options.ProductWriteEnabledClasses.Contains("messenger-delivery", StringComparer.Ordinal);

    private sealed record SnapshotCleanupProof(string RootIdentity, string FileIdentity, long Bytes, string Sha256);

    private async Task<SnapshotCleanupProof?> CaptureSnapshotCleanupProofAsync(FileStream target,
        string? rootIdentity, string? fileIdentity, CancellationToken ct)
    {
        var root = Path.GetDirectoryName(_snapshotRoot)!;
        if (rootIdentity is null || fileIdentity is null || StorageStageRootIdentity.Observe(root) != rootIdentity)
            return null;
        await target.FlushAsync(ct);
        var size = target.Length;
        target.Position = 0;
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(target, ct)).ToLowerInvariant();
        if (target.Length != size || StorageStageFileIdentity.Observe(target.SafeFileHandle) != fileIdentity
            || StorageStageRootIdentity.Observe(root) != rootIdentity) return null;
        return new(rootIdentity, fileIdentity, size, sha256);
    }

    private Task<bool> TryDeletePreparedSnapshotAsync(string path, StorageStageCapacityTicket? ticket,
        StorageStageFileReceipt? receipt, SnapshotCleanupProof? legacyProof)
        => TryDeleteSnapshotCleanupProofAsync(path, GetSnapshotCleanupProof(ticket, receipt, legacyProof));

    private static SnapshotCleanupProof? GetSnapshotCleanupProof(StorageStageCapacityTicket? ticket,
        StorageStageFileReceipt? receipt, SnapshotCleanupProof? legacyProof)
    {
        if (ticket is null) return legacyProof;
        return ticket.RootIdentity is { } rootIdentity && receipt is not null
            ? new(rootIdentity, receipt.FileIdentity, receipt.PlannedBytes, receipt.PlannedSha256)
            : null;
    }

    private Task<bool> TryDeleteSnapshotCleanupProofAsync(string path, SnapshotCleanupProof? proof)
    {
        // Initial proof can clean only the unchanged empty file; incomplete
        // physical prefixes never match it and keep their full pending debt.
        var root = Path.GetDirectoryName(_snapshotRoot)!;
        if (proof is null || StorageStageRootIdentity.Observe(root) != proof.RootIdentity)
            return Task.FromResult(false);
        return VerifiedOriginalReadFile.TryDeleteAsync(path, proof.FileIdentity, proof.Bytes, proof.Sha256);
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
public sealed record MessengerDocumentDownload(string FullPath, string ContentType, string FileName, string ChecksumSha256,
    Stream? Content = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}
