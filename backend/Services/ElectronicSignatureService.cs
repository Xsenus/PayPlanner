using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Services;

public sealed record ElectronicSignatureSignerDraft(
    string DisplayName,
    string Contact,
    string PartyType = "client",
    int? ClientPortalAccountId = null,
    bool RequiresMachineReadablePowerOfAttorney = false,
    string? PowerOfAttorneyReference = null);

public sealed record ElectronicSignaturePreparation(
    string DocumentType,
    int DocumentId,
    int DocumentVersion,
    int? ClientId,
    string SignatureType,
    string SigningOrder,
    string Provider,
    int ArchiveRetentionYears,
    bool RequiresMachineReadablePowerOfAttorney,
    int CreatedByUserId,
    IReadOnlyCollection<ElectronicSignatureSignerDraft> Signers);

public sealed record ElectronicSignatureProviderResult(
    bool Success,
    string? ProviderEnvelopeId = null,
    string? Error = null,
    bool IsTransient = false);

public sealed record ElectronicSignatureEvidencePackage(string FullPath, string FileName)
{
    private readonly string? _rootPath, _rootIdentity, _fileIdentity, _sha256;
    private readonly long _size;
    private readonly Action? _retained;

    internal ElectronicSignatureEvidencePackage(string fullPath, string fileName, string rootPath,
        string? rootIdentity, string? fileIdentity, long size, string sha256, Action retained)
        : this(fullPath, fileName)
        => (_rootPath, _rootIdentity, _fileIdentity, _size, _sha256, _retained)
            = (rootPath, rootIdentity, fileIdentity, size, sha256, retained);

    internal async Task<FileStream> OpenReadAsync(CancellationToken ct)
    {
        if (_rootPath is null || _rootIdentity is null || _sha256 is null
            || StorageStageRootIdentity.Observe(_rootPath) != _rootIdentity)
            throw new InvalidOperationException("Не удалось подтвердить временный доказательный архив.");
        var file = await VerifiedOriginalReadFile.OpenVerifiedAsync(FullPath, _fileIdentity, _size,
            _sha256, _retained ?? (() => { }), ct);
        if (file is null)
            throw new InvalidOperationException("Временный доказательный архив изменён или недоступен.");
        // Verify the directory again after asynchronous byte verification.
        // Disposal owns only this proven object, never a replacement path.
        if (StorageStageRootIdentity.Observe(_rootPath) != _rootIdentity)
        {
            await file.DisposeAsync();
            throw new InvalidOperationException("Каталог временного доказательного архива изменён.");
        }
        return file;
    }
}

public interface IElectronicSignatureProvider
{
    string Name { get; }
    Task<ElectronicSignatureProviderResult> CreateEnvelopeAsync(
        ElectronicSignatureEnvelope envelope,
        Stream document,
        CancellationToken ct);
}

public sealed class DisabledElectronicSignatureProvider : IElectronicSignatureProvider
{
    public DisabledElectronicSignatureProvider(string name) => Name = name;
    public string Name { get; }

    public Task<ElectronicSignatureProviderResult> CreateEnvelopeAsync(
        ElectronicSignatureEnvelope envelope,
        Stream document,
        CancellationToken ct) => Task.FromResult(new ElectronicSignatureProviderResult(
            false,
            Error: $"Провайдер электронной подписи {Name} не подключён."));
}

public static class ElectronicSignatureStateMachine
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> Allowed =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [ElectronicSignatureEnvelopeStatuses.Draft] = [ElectronicSignatureEnvelopeStatuses.Prepared, ElectronicSignatureEnvelopeStatuses.Cancelled],
            [ElectronicSignatureEnvelopeStatuses.Prepared] = [ElectronicSignatureEnvelopeStatuses.Sending, ElectronicSignatureEnvelopeStatuses.Cancelled],
            [ElectronicSignatureEnvelopeStatuses.Sending] = [ElectronicSignatureEnvelopeStatuses.Pending, ElectronicSignatureEnvelopeStatuses.Failed],
            [ElectronicSignatureEnvelopeStatuses.Pending] =
            [
                ElectronicSignatureEnvelopeStatuses.Completed,
                ElectronicSignatureEnvelopeStatuses.Declined,
                ElectronicSignatureEnvelopeStatuses.Expired,
                ElectronicSignatureEnvelopeStatuses.Cancelled,
                ElectronicSignatureEnvelopeStatuses.Failed,
                ElectronicSignatureEnvelopeStatuses.VerificationFailed
            ],
            [ElectronicSignatureEnvelopeStatuses.Failed] = [ElectronicSignatureEnvelopeStatuses.Sending, ElectronicSignatureEnvelopeStatuses.Cancelled],
            [ElectronicSignatureEnvelopeStatuses.VerificationFailed] = [ElectronicSignatureEnvelopeStatuses.Pending, ElectronicSignatureEnvelopeStatuses.Cancelled]
        };

    public static bool CanTransition(string current, string next) =>
        !string.Equals(current, next, StringComparison.OrdinalIgnoreCase)
        && Allowed.TryGetValue(current, out var targets)
        && targets.Contains(next);

    public static void EnsureTransition(ElectronicSignatureEnvelope envelope, string next)
    {
        if (!CanTransition(envelope.Status, next))
        {
            throw new InvalidOperationException($"Переход электронной подписи {envelope.Status} -> {next} запрещён.");
        }

        if (string.Equals(next, ElectronicSignatureEnvelopeStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            && (!envelope.Artifacts.Any()
                || envelope.Artifacts.Any(item => !string.Equals(item.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("Нельзя завершить подписание без проверенных криптографических артефактов.");
        }
    }
}

public interface IElectronicSignatureService
{
    Task<ElectronicSignatureEnvelope> PrepareAsync(
        ElectronicSignaturePreparation request,
        Stream pdf,
        string fileName,
        CancellationToken ct);

    Task<ElectronicSignatureEnvelope?> CancelPreparedAsync(
        Guid publicId,
        int actorUserId,
        string? reason,
        CancellationToken ct);

    Task<ElectronicSignatureEvidencePackage?> CreateEvidencePackageAsync(
        Guid publicId,
        CancellationToken ct);
}

public sealed class ElectronicSignatureService : IElectronicSignatureService
{
    private const long MaxPdfSize = 25L * 1024 * 1024;
    private const long MaxEvidencePackageSourceSize = 100L * 1024 * 1024;
    private const int MaxEvidenceArtifacts = 100;
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly HashSet<string> SignatureTypes =
    [
        ElectronicSignatureTypes.Simple,
        ElectronicSignatureTypes.EnhancedUnqualified,
        ElectronicSignatureTypes.Qualified
    ];
    private static readonly HashSet<string> SigningOrders =
    [
        ElectronicSignatureSigningOrders.Sequential,
        ElectronicSignatureSigningOrders.Parallel
    ];
    private static readonly HashSet<string> DocumentTypes = ["invoice", "act", "contract"];
    private static readonly HashSet<string> ProviderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "KonturDiadoc", "Sbis", "Taxcom"
    };
    private static readonly HashSet<string> PartyTypes = ["client", "representative"];

    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;
    private readonly IDataProtector _contactProtector;
    private readonly string _storageRoot;
    private readonly string _legacyStorageRoot;
    private readonly IProductStorageReader? _productStorageReader;
    private readonly ILogger<ElectronicSignatureService>? _logger;
    private readonly IStorageObjectService? _managedStorage;
    private readonly IProductStorageBindingService? _storagePublisher;
    private readonly IOptionsMonitor<StorageOrchestrationOptions>? _storageOptions;
    private readonly IStorageStageCapacityService? _stageCapacity;

    public ElectronicSignatureService(
        PaymentContext db,
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        IWebHostEnvironment environment,
        IProductStorageReader? productStorageReader = null,
        ILogger<ElectronicSignatureService>? logger = null,
        IStorageObjectService? managedStorage = null,
        IProductStorageBindingService? storagePublisher = null,
        IOptionsMonitor<StorageOrchestrationOptions>? storageOptions = null,
        IStorageStageCapacityService? stageCapacity = null)
    {
        _db = db;
        _configuration = configuration;
        _productStorageReader = productStorageReader;
        _logger = logger;
        _managedStorage = managedStorage;
        _storagePublisher = storagePublisher;
        _storageOptions = storageOptions;
        _stageCapacity = stageCapacity;
        _contactProtector = dataProtectionProvider.CreateProtector("PayPlanner.ElectronicSignature.Contact.v1");
        _storageRoot = ElectronicSignatureStoragePaths.ResolveRoot(environment, configuration);
        _legacyStorageRoot = ElectronicSignatureStoragePaths.ResolveLegacyRoot(environment);
    }

    public async Task<ElectronicSignatureEnvelope> PrepareAsync(
        ElectronicSignaturePreparation request,
        Stream pdf,
        string fileName,
        CancellationToken ct)
    {
        Validate(request, pdf, fileName);
        var signaturePolicy = CaptureSignaturePolicy();
        Directory.CreateDirectory(_storageRoot);

        var temporaryPath = Path.Combine(_storageRoot, $".{Guid.NewGuid():N}.tmp");
        string checksum = string.Empty;
        long size = 0;
        string? idempotencyKey = null;
        var preparationRollbackConfirmed = false;
        StorageStageCapacityTicket? stageTicket = null;
        var temporaryCreated = false;
        StorageStageFileReceipt? initialPdfCheckpoint = null, completedPdfCheckpoint = null;
        string? legacyPdfRootIdentity = null, legacyPdfFileIdentity = null, legacyPdfSha256 = null;
        long legacyPdfBytes = 0;
        VerifiedSignatureSnapshot? originalSnapshot = null;
        Task<bool> CleanupOwnedPdfAsync(string path)
        {
            if (stageTicket is not null)
            {
                var known = completedPdfCheckpoint ?? initialPdfCheckpoint;
                return TryCleanupSignatureTemporaryFileAsync(path, _storageRoot, stageTicket.RootIdentity,
                    known?.FileIdentity, known?.PlannedBytes ?? 0, known?.PlannedSha256);
            }
            return TryCleanupSignatureTemporaryFileAsync(path, _storageRoot, legacyPdfRootIdentity,
                legacyPdfFileIdentity, legacyPdfBytes, legacyPdfSha256);
        }
        try
        {
            RequireSignaturePolicy(signaturePolicy);
            if (ManagedOriginalWritesEnabled()
                && (_managedStorage is null || _storagePublisher is null
                    || _storageOptions?.CurrentValue.UploadCapacityChecksEnabled != true))
                throw new InvalidOperationException("Для управляемого хранения PDF подписи настройте хранилище и учёт места загрузок.");
            if (_storageOptions?.CurrentValue.UploadCapacityChecksEnabled == true)
            {
                if (_stageCapacity is null)
                    throw new InvalidOperationException("Учёт места для подготовки PDF подписи не настроен.");
                var upperBound = pdf.CanSeek ? pdf.Length - pdf.Position : MaxPdfSize;
                if (upperBound <= 0 || upperBound > MaxPdfSize)
                    throw new InvalidOperationException("Размер PDF должен быть от 1 байта до 25 МБ.");
                var admission = await _stageCapacity.ReserveCompatibilityAsync(
                    "esign-pdf:" + Path.GetFileName(temporaryPath), temporaryPath, upperBound, "esign-current", ct);
                stageTicket = admission.Ticket;
                if (stageTicket is null)
                    throw new InvalidOperationException("Недостаточно подтверждённого места для подготовки PDF подписи.");
            }
            async Task WritePdfAsync(Stream output, CancellationToken token)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var header = new byte[5];
                var headerRead = await ReadAtMostAsync(pdf, header, token);
                if (headerRead < 5 || !header.AsSpan().SequenceEqual("%PDF-"u8))
                {
                    throw new InvalidOperationException("Разрешены только корректные PDF-документы.");
                }

                RequireSignaturePolicy(signaturePolicy);
                await output.WriteAsync(header, token);
                hash.AppendData(header);
                size = header.Length;
                var buffer = new byte[81920];
                int read;
                while ((read = await pdf.ReadAsync(buffer, token)) > 0)
                {
                    size += read;
                    if (size > MaxPdfSize)
                    {
                        throw new InvalidOperationException("Размер PDF превышает 25 МБ.");
                    }
                    hash.AppendData(buffer.AsSpan(0, read));
                    RequireSignaturePolicy(signaturePolicy);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                RequireSignaturePolicy(signaturePolicy);
                checksum = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            if (stageTicket is not null)
            {
                completedPdfCheckpoint = await _stageCapacity!.WriteCheckpointedFileAsync(stageTicket,
                    async (output, initial, token) =>
                    {
                        temporaryCreated = true;
                        initialPdfCheckpoint = initial;
                        await WritePdfAsync(output, token);
                    }, ct);
            }
            else
            {
                legacyPdfRootIdentity = StorageStageRootIdentity.Observe(_storageRoot);
                await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
                temporaryCreated = true;
                var originalIdentity = StorageStageFileIdentity.Observe(output.SafeFileHandle);
                async Task<bool> CaptureOwnedPdfBytesAsync(CancellationToken token)
                {
                    if (originalIdentity is null || legacyPdfRootIdentity is null
                        || StorageStageRootIdentity.Observe(_storageRoot) != legacyPdfRootIdentity) return false;
                    await output.FlushAsync(token);
                    var observedSize = output.Length;
                    output.Position = 0;
                    var observedSha256 = Convert.ToHexString(await SHA256.HashDataAsync(output, token)).ToLowerInvariant();
                    if (output.Length != observedSize || StorageStageFileIdentity.Observe(output.SafeFileHandle) != originalIdentity
                        || StorageStageRootIdentity.Observe(_storageRoot) != legacyPdfRootIdentity) return false;
                    legacyPdfFileIdentity = originalIdentity;
                    legacyPdfBytes = observedSize;
                    legacyPdfSha256 = observedSha256;
                    return true;
                }
                try
                {
                    await WritePdfAsync(output, ct);
                    if (!await CaptureOwnedPdfBytesAsync(ct))
                        throw new InvalidOperationException("Не удалось подтвердить временный PDF подписи.");
                    if (legacyPdfBytes != size || !string.Equals(legacyPdfSha256, checksum, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Содержимое временного PDF подписи изменилось.");
                }
                catch
                {
                    // Capture failed bytes through the original descriptor, even
                    // when the caller cancelled. Unknown files stay untouched.
                    if (originalIdentity is not null && legacyPdfRootIdentity is not null
                        && StorageStageRootIdentity.Observe(_storageRoot) == legacyPdfRootIdentity)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try
                        {
                            await CaptureOwnedPdfBytesAsync(cleanup.Token);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                        {
                            _logger?.LogWarning(error, "Could not verify failed signature PDF bytes: {TemporaryPath}", temporaryPath);
                        }
                    }
                    throw;
                }
            }

            if (stageTicket is not null)
            {
                stageTicket = await _stageCapacity!.ConfirmPhysicalSizeAsync(stageTicket, size, ct);
                await _stageCapacity.SettlePhysicalAsync(stageTicket, ct);
            }

            RequireSignaturePolicy(signaturePolicy);
            idempotencyKey = BuildIdempotencyKey(request, checksum);
            var existing = await _db.ElectronicSignatureEnvelopes
                .AsNoTracking()
                .Include(item => item.Signers)
                .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                if (!MatchesEnvelopePreparation(existing, request, fileName, size, checksum))
                    throw new InvalidOperationException("Параметры подготовки подписи изменились. Используйте исходную подготовку или другую версию документа.");
                if (signaturePolicy.Managed)
                {
                    if (_storagePublisher is not IProductStoragePublishedReceiptVerifier verifier
                        || !await verifier.VerifyPublishedReceiptAsync(new(LegacyStorageSources.ElectronicSignatureOriginals,
                            existing.Id, existing.OriginalFileName, existing.ContentType, existing.SizeBytes, existing.ChecksumSha256,
                            existing.CreatedByUserId, $"esign-original:{idempotencyKey}:{existing.CreatedByUserId}"), ct))
                        throw new InvalidOperationException("Хранилище не подтвердило обязательные копии исходного PDF подписи. Повторите подготовку после восстановления хранилища.");
                }
                RequireSignaturePolicy(signaturePolicy);
                await CleanupOwnedPdfAsync(temporaryPath);
                return existing;
            }

            var publicId = Guid.NewGuid();
            var originalProof = completedPdfCheckpoint ?? initialPdfCheckpoint;
            originalSnapshot = await VerifiedSignatureSnapshot.MoveAsync(temporaryPath, _storageRoot,
                stageTicket is not null ? stageTicket.RootIdentity : legacyPdfRootIdentity,
                stageTicket is not null ? originalProof?.FileIdentity : legacyPdfFileIdentity,
                size, checksum, publicId.ToString("N"), ct);
            if (originalSnapshot is null)
                throw new InvalidOperationException("Не удалось подтвердить исходный PDF перед регистрацией подписи.");
            var destinationPath = originalSnapshot.FullPath;

            var now = DateTime.UtcNow;
            var envelope = new ElectronicSignatureEnvelope
            {
                PublicId = publicId,
                DocumentType = request.DocumentType.Trim().ToLowerInvariant(),
                DocumentId = request.DocumentId,
                DocumentVersion = request.DocumentVersion,
                ClientId = request.ClientId,
                SignatureType = request.SignatureType.Trim().ToLowerInvariant(),
                SigningOrder = request.SigningOrder.Trim().ToLowerInvariant(),
                Provider = request.Provider.Trim(),
                Status = ElectronicSignatureEnvelopeStatuses.Prepared,
                OriginalFileName = Path.GetFileName(fileName),
                SnapshotPath = destinationPath,
                SizeBytes = size,
                ChecksumSha256 = checksum,
                IdempotencyKey = idempotencyKey,
                ArchiveRetentionYears = request.ArchiveRetentionYears,
                RequiresMachineReadablePowerOfAttorney = request.RequiresMachineReadablePowerOfAttorney,
                CreatedByUserId = request.CreatedByUserId,
                CreatedAt = now,
                UpdatedAt = now,
                Signers = request.Signers.Select((signer, index) => new ElectronicSignatureSigner
                {
                    Order = index + 1,
                    PartyType = signer.PartyType.Trim().ToLowerInvariant(),
                    ClientPortalAccountId = signer.ClientPortalAccountId,
                    DisplayName = signer.DisplayName.Trim(),
                    ProtectedContact = _contactProtector.Protect(signer.Contact.Trim()),
                    MaskedContact = MaskContact(signer.Contact),
                    RequiresMachineReadablePowerOfAttorney = signer.RequiresMachineReadablePowerOfAttorney,
                    PowerOfAttorneyReference = signer.PowerOfAttorneyReference?.Trim()
                }).ToList()
            };
            envelope.Events.Add(new ElectronicSignatureEvent
            {
                EventType = "prepared",
                Status = envelope.Status,
                Message = "Создан неизменяемый снимок документа для подписания.",
                OccurredAt = now,
                ReceivedAt = now
            });
            IDbContextTransaction? transaction = null;
            var commitStarted = false;
            try
            {
                RequireSignaturePolicy(signaturePolicy);
                StorageObjectWriteResult? managed = null;
                var pendingOwner = $"upload:esign:{idempotencyKey}:{request.CreatedByUserId}";
                RequireSignaturePolicy(signaturePolicy);
                if (ManagedOriginalWritesEnabled())
                {
                    if (_managedStorage is null || _storagePublisher is null)
                        throw new InvalidOperationException("Управляемое хранилище электронной подписи не настроено.");
                    if (_storageOptions?.CurrentValue.UploadCapacityChecksEnabled != true)
                        throw new InvalidOperationException("Для управляемого хранения PDF подписи включите долговечное резервирование места загрузок.");
                    // A content-keyed reservation survives an unknown provider outcome.
                    // All provider I/O completes before the domain transaction begins.
                    managed = await _managedStorage.WriteAsync(new("esign-original", "esign-original", pendingOwner,
                        envelope.OriginalFileName, "application/pdf", originalSnapshot.Content,
                        $"esign-original:{idempotencyKey}:{request.CreatedByUserId}", request.CreatedByUserId,
                        ExpectedSizeBytes: size, ExpectedSha256: checksum), ct);
                    if (managed.ObjectId == Guid.Empty || managed.Status != "available")
                        throw new InvalidOperationException("Хранилище ещё не подтвердило сохранение PDF. Повторите подготовку подписи после восстановления хранилища.");
                }
                RequireSignaturePolicy(signaturePolicy);
                if (!await originalSnapshot.VerifyAsync(ct))
                    throw new InvalidOperationException("Исходный PDF подписи изменился перед сохранением регистрации.");
                transaction = await _db.Database.BeginTransactionAsync(ct);
                _db.ElectronicSignatureEnvelopes.Add(envelope);
                await _db.SaveChangesAsync(ct);
                if (managed is not null)
                {
                    if (!ManagedOriginalWritesEnabled())
                        throw new InvalidOperationException("Режим хранения изменился во время подготовки подписи.");
                    var owner = envelope.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var changed = await _db.StorageObjects.Where(item => item.Id == managed.ObjectId && !item.IsDeleted
                            && item.DataClass == "esign-original" && item.OwnerType == "esign-original"
                            && item.OwnerId == pendingOwner && item.LogicalName == envelope.OriginalFileName && item.ContentType == "application/pdf"
                            && item.Version < long.MaxValue && item.CurrentGeneration == managed.Generation
                            && _db.StorageObjectGenerations.Any(generation => generation.StorageObjectId == item.Id
                                && generation.Generation == managed.Generation && generation.SizeBytes == size && generation.Sha256 == checksum))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.OwnerId, owner)
                            .SetProperty(item => item.Version, item => item.Version + 1), ct);
                    if (changed != 1)
                        throw new InvalidOperationException("Не удалось подтвердить версию исходного PDF в хранилище.");
                    var published = await _storagePublisher!.PublishAsync(new(LegacyStorageSources.ElectronicSignatureOriginals,
                        envelope.Id, managed.ObjectId, envelope.OriginalFileName, "application/pdf", 0), ct);
                    if (!published.Published)
                        throw new InvalidOperationException("Обязательные копии исходного PDF ещё не подтверждены.");
                }
                RequireSignaturePolicy(signaturePolicy);
                if (!await originalSnapshot.VerifyAsync(ct))
                    throw new InvalidOperationException("Исходный PDF подписи изменился перед подтверждением регистрации.");
                commitStarted = true;
                await transaction.CommitAsync(ct);
                return envelope;
            }
            catch
            {
                // Once commit starts, a lost acknowledgement must not delete a linked PDF.
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
                            // Preserve the snapshot when rollback cannot be confirmed.
                            _logger?.LogWarning(exception, "Could not confirm rollback of signature snapshot registration");
                        }
                    }
                    preparationRollbackConfirmed = rollbackConfirmed;
                    if (rollbackConfirmed)
                    {
                        await originalSnapshot.DisposeAsync();
                        originalSnapshot = null;
                        await CleanupOwnedPdfAsync(destinationPath);
                    }
                }
                // Commit may fail before reaching the database or lose its ACK.
                // Do not keep rolled-back generated IDs tracked across an exact
                // retry; durable rows and bytes remain authoritative in either case.
                foreach (var signer in envelope.Signers.ToArray()) _db.Entry(signer).State = EntityState.Detached;
                foreach (var signatureEvent in envelope.Events.ToArray()) _db.Entry(signatureEvent).State = EntityState.Detached;
                _db.Entry(envelope).State = EntityState.Detached;
                throw;
            }
            finally
            {
                if (transaction is not null) await transaction.DisposeAsync();
            }
        }
        catch (Exception exception) when (preparationRollbackConfirmed && idempotencyKey is not null
            && (exception is DbUpdateException update && IsPreparationKeyCollision(update)
                || StorageObjectService.IsReplayOwnerMismatch(exception, $"esign-original:{idempotencyKey}:{request.CreatedByUserId}")))
        {
            // The failed graph has been detached and its transaction disposed.
            // Only the exact unique constraint or a marked completed owner
            // handoff permit recovery; unknown acknowledgements remain failures.
            RequireSignaturePolicy(signaturePolicy);
            var winner = await _db.ElectronicSignatureEnvelopes.AsNoTracking()
                .Include(item => item.Signers)
                .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
            if (winner is null || !MatchesPreparedEnvelope(winner, request, fileName, size, checksum))
                throw;
            if (signaturePolicy.Managed)
            {
                if (_storagePublisher is not IProductStoragePublishedReceiptVerifier verifier
                    || !await verifier.VerifyPublishedReceiptAsync(new(LegacyStorageSources.ElectronicSignatureOriginals,
                        winner.Id, winner.OriginalFileName, winner.ContentType, winner.SizeBytes, winner.ChecksumSha256,
                        winner.CreatedByUserId, $"esign-original:{idempotencyKey}:{winner.CreatedByUserId}"), ct))
                    throw;
            }
            RequireSignaturePolicy(signaturePolicy);
            return winner;
        }
        catch
        {
            if (temporaryCreated) await CleanupOwnedPdfAsync(temporaryPath);
            throw;
        }
        finally
        {
            try
            {
                if (originalSnapshot is not null) await originalSnapshot.DisposeAsync();
                if (stageTicket is not null && !File.Exists(temporaryPath))
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _stageCapacity!.ReleaseAsync(stageTicket, cleanup.Token);
                    }
                    catch (Exception exception)
                    {
                        // Pending debt survives a lost confirmation acknowledgement for bounded recovery.
                        _logger?.LogWarning(exception, "Could not release signature PDF stage capacity {ReservationKey}", stageTicket.Key);
                    }
                }
            }
            finally
            {
                if (stageTicket is not null) await stageTicket.CloseWriterAsync();
            }
        }
    }

    private bool MatchesPreparedEnvelope(ElectronicSignatureEnvelope envelope, ElectronicSignaturePreparation request,
        string fileName, long size, string checksum)
        => envelope.Status == ElectronicSignatureEnvelopeStatuses.Prepared && envelope.CancelledAt is null
            && envelope.CreatedByUserId == request.CreatedByUserId
            && envelope.Signers.All(signer => signer.Status == "pending")
            && MatchesEnvelopePreparation(envelope, request, fileName, size, checksum);

    private bool MatchesEnvelopePreparation(ElectronicSignatureEnvelope envelope, ElectronicSignaturePreparation request,
        string fileName, long size, string checksum)
    {
        if (envelope.ClientId != request.ClientId
            || envelope.DocumentType != request.DocumentType.Trim().ToLowerInvariant()
            || envelope.DocumentId != request.DocumentId || envelope.DocumentVersion != request.DocumentVersion
            || envelope.SignatureType != request.SignatureType.Trim().ToLowerInvariant()
            || envelope.SigningOrder != request.SigningOrder.Trim().ToLowerInvariant() || envelope.Provider != request.Provider.Trim()
            || envelope.ArchiveRetentionYears != request.ArchiveRetentionYears
            || envelope.RequiresMachineReadablePowerOfAttorney != request.RequiresMachineReadablePowerOfAttorney
            || envelope.OriginalFileName != Path.GetFileName(fileName) || envelope.ContentType != "application/pdf"
            || envelope.SizeBytes != size || !string.Equals(envelope.ChecksumSha256, checksum, StringComparison.OrdinalIgnoreCase)
            || envelope.Signers.Count != request.Signers.Count) return false;
        var signers = envelope.Signers.OrderBy(item => item.Order).ToArray();
        var index = 0;
        foreach (var expected in request.Signers)
        {
            var actual = signers[index++];
            if (actual.Order != index
                || actual.PartyType != expected.PartyType.Trim().ToLowerInvariant()
                || actual.ClientPortalAccountId != expected.ClientPortalAccountId || actual.DisplayName != expected.DisplayName.Trim()
                || actual.RequiresMachineReadablePowerOfAttorney != expected.RequiresMachineReadablePowerOfAttorney
                || actual.PowerOfAttorneyReference != expected.PowerOfAttorneyReference?.Trim()) return false;
            try
            {
                if (!string.Equals(_contactProtector.Unprotect(actual.ProtectedContact), expected.Contact.Trim(),
                    StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (CryptographicException) { return false; }
        }
        return true;
    }

    private static bool IsPreparationKeyCollision(DbUpdateException exception) => exception.InnerException switch
    {
        Npgsql.PostgresException pg => pg.SqlState == "23505"
            && pg.ConstraintName == "IX_ElectronicSignatureEnvelopes_IdempotencyKey",
        Microsoft.Data.Sqlite.SqliteException sqlite => sqlite.SqliteErrorCode == 19
            && sqlite.SqliteExtendedErrorCode == 2067
            && sqlite.Message.Contains("ElectronicSignatureEnvelopes.IdempotencyKey", StringComparison.Ordinal),
        _ => false
    };

    private (bool Managed, bool Capacity) CaptureSignaturePolicy()
    {
        var configured = _storageOptions?.CurrentValue;
        return (configured is { Enabled: true } && configured.ProductWriteEnabledClasses.Contains("esign-original", StringComparer.Ordinal),
            configured?.UploadCapacityChecksEnabled == true);
    }

    private void RequireSignaturePolicy((bool Managed, bool Capacity) expected)
    {
        if (CaptureSignaturePolicy() != expected)
            throw new InvalidOperationException("Настройки хранения изменились во время подготовки подписи. Повторите подготовку.");
    }

    private bool ManagedOriginalWritesEnabled()
        => _storageOptions?.CurrentValue is { Enabled: true } options
            && options.ProductWriteEnabledClasses.Contains("esign-original", StringComparer.Ordinal);

    public async Task<ElectronicSignatureEnvelope?> CancelPreparedAsync(
        Guid publicId,
        int actorUserId,
        string? reason,
        CancellationToken ct)
    {
        if (publicId == Guid.Empty || actorUserId <= 0)
            throw new InvalidOperationException("Не удалось определить версию или пользователя.");

        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length > 1000)
            throw new InvalidOperationException("Причина отмены не должна превышать 1000 символов.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var updated = await _db.ElectronicSignatureEnvelopes
            .Where(item => item.PublicId == publicId
                && item.Status == ElectronicSignatureEnvelopeStatuses.Prepared)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, ElectronicSignatureEnvelopeStatuses.Cancelled)
                .SetProperty(item => item.CancelledAt, now)
                .SetProperty(item => item.UpdatedAt, now), ct);

        var envelope = await _db.ElectronicSignatureEnvelopes
            .AsNoTracking()
            .Include(item => item.Signers)
            .Include(item => item.Events)
            .FirstOrDefaultAsync(item => item.PublicId == publicId, ct);
        if (envelope is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        if (updated == 0)
        {
            if (!string.Equals(envelope.Status, ElectronicSignatureEnvelopeStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Отменить можно только подготовленную и ещё не отправленную версию.");

            await transaction.CommitAsync(ct);
            return envelope;
        }

        var auditEvent = new ElectronicSignatureEvent
        {
            EnvelopeId = envelope.Id,
            EventType = "cancelled-by-user",
            Status = ElectronicSignatureEnvelopeStatuses.Cancelled,
            Message = string.IsNullOrWhiteSpace(normalizedReason)
                ? $"Подготовленная версия отменена пользователем #{actorUserId}."
                : $"Подготовленная версия отменена пользователем #{actorUserId}. Причина: {normalizedReason}",
            OccurredAt = now,
            ReceivedAt = now
        };
        _db.ElectronicSignatureEvents.Add(auditEvent);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        envelope.Events.Add(auditEvent);
        return envelope;
    }

    public async Task<ElectronicSignatureEvidencePackage?> CreateEvidencePackageAsync(
        Guid publicId,
        CancellationToken ct)
    {
        if (publicId == Guid.Empty)
            throw new InvalidOperationException("Не удалось определить версию электронной подписи.");

        var signaturePolicy = CaptureSignaturePolicy();
        var envelope = await _db.ElectronicSignatureEnvelopes
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Signers)
            .Include(item => item.Events)
            .Include(item => item.Artifacts)
            .FirstOrDefaultAsync(item => item.PublicId == publicId, ct);
        if (envelope is null) return null;
        if (envelope.Artifacts.Count > MaxEvidenceArtifacts)
            throw new InvalidOperationException("Доказательный пакет содержит слишком много артефактов.");

        var sources = new List<EvidenceSource>
        {
            new("original/document.pdf", envelope.SnapshotPath, envelope.ChecksumSha256, "original",
                LegacyStorageSources.ElectronicSignatureOriginals, envelope.Id, envelope.OriginalFileName,
                "application/pdf", envelope.SizeBytes)
        };
        sources.AddRange(envelope.Artifacts
            .OrderBy(item => item.Id)
            .Select((item, index) => new EvidenceSource(
                $"artifacts/{index + 1:D3}-{SanitizeEntryName(item.FileName)}",
                item.StoragePath,
                item.ChecksumSha256,
                item.ArtifactType, LegacyStorageSources.ElectronicSignatures, item.Id,
                item.FileName, item.ContentType, item.SizeBytes)));

        long totalSourceSize = 0;
        foreach (var source in sources)
        {
            if (source.SizeBytes < 0 || source.SizeBytes > MaxEvidencePackageSourceSize - totalSourceSize)
                throw new InvalidOperationException("Общий размер файлов доказательного пакета превышает 100 МБ.");
            totalSourceSize += source.SizeBytes;
        }

        var files = sources.Select(source => new { path = source.EntryName, type = source.ArtifactType, sizeBytes = source.SizeBytes, checksumSha256 = source.ExpectedChecksum.ToLowerInvariant() }).ToArray();
        var manifest = new
        {
            format = "payplanner-electronic-signature-evidence",
            formatVersion = 1,
            generatedAt = DateTime.UtcNow,
            envelope = new
            {
                envelope.PublicId,
                envelope.DocumentType,
                envelope.DocumentId,
                envelope.DocumentVersion,
                envelope.SignatureType,
                envelope.SigningOrder,
                envelope.Provider,
                envelope.ProviderEnvelopeId,
                envelope.Status,
                envelope.ArchiveRetentionYears,
                envelope.CreatedAt,
                envelope.SentAt,
                envelope.CompletedAt,
                envelope.CancelledAt
            },
            signers = envelope.Signers.OrderBy(item => item.Order).Select(item => new
            {
                item.Order,
                item.PartyType,
                item.DisplayName,
                item.MaskedContact,
                item.Status,
                item.RequiresMachineReadablePowerOfAttorney,
                item.PowerOfAttorneyReference,
                item.CertificateThumbprint,
                item.CertificateSubject,
                item.CertificateIssuer,
                item.CertificateValidFrom,
                item.CertificateValidTo,
                item.CertificateRevocationStatus,
                item.InvitedAt,
                item.SignedAt,
                item.DeclinedAt,
                item.DeclineReason
            }),
            events = envelope.Events.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Select(item => new
            {
                item.ProviderEventId,
                item.EventType,
                item.Status,
                item.Message,
                item.PayloadHash,
                item.OccurredAt,
                item.ReceivedAt
            }),
            files
        };
        long? upperBound = null;
        if (signaturePolicy.Capacity)
        {
            if (_stageCapacity is null)
                throw new InvalidOperationException("Проверка места для доказательного архива не настроена.");
            using var counter = new EvidenceArchiveWriteStream(null, long.MaxValue, () => RequireSignaturePolicy(signaturePolicy));
            await JsonSerializer.SerializeAsync(counter, manifest, EvidenceJsonOptions, ct);
            upperBound = checked(1024 + EvidenceEntryUpperBound("manifest.json", counter.Length));
            foreach (var source in sources)
                upperBound = checked(upperBound.Value + EvidenceEntryUpperBound(source.EntryName, source.SizeBytes));
        }
        RequireSignaturePolicy(signaturePolicy);
        var exportsDirectory = Path.Combine(_storageRoot, ".exports");
        Directory.CreateDirectory(exportsDirectory);
        // Age does not prove that an earlier file belongs to a stopped writer,
        // or that its durable reservation was released. Unknown/crashed exports
        // stay intact until recovery can establish that proof. The HTTP owner
        // still removes its own completed package after sending the response.
        var packagePath = Path.Combine(exportsDirectory, $"{publicId:N}-{Guid.NewGuid():N}.zip");
        StorageStageCapacityTicket? stageTicket = null;
        var packageCreated = false;
        StorageStageFileReceipt? initialCheckpoint = null, completedCheckpoint = null;
        var packageRootIdentity = StorageStageRootIdentity.Observe(_storageRoot);
        string? completedFileIdentity = null, completedSha256 = null;
        long completedBytes = 0;
        try
        {
            RequireSignaturePolicy(signaturePolicy);
            if (upperBound is { } admitted)
            {
                var result = await _stageCapacity!.ReserveCompatibilityAsync($"esign-evidence:{publicId:N}:{Path.GetFileName(packagePath)}",
                    packagePath, admitted, "esign-current", ct);
                stageTicket = result.Ticket;
                if (stageTicket is null) throw new InvalidOperationException(result.Error ?? "Недостаточно места для доказательного архива.");
            }
            RequireSignaturePolicy(signaturePolicy);
            async Task WriteArchiveAsync(Stream output, CancellationToken token)
            {
                long copiedSize = 0;
                // ZIP entry Flush validates policy; the checkpoint writer owns
                // bounded physical flushes and the final durable boundary.
                using var bounded = new EvidenceArchiveWriteStream(output, stageTicket?.SizeBytes ?? long.MaxValue,
                    () => RequireSignaturePolicy(signaturePolicy), flushInner: stageTicket is null);
                using var archive = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
                foreach (var source in sources)
                {
                    var entry = archive.CreateEntry(source.EntryName, CompressionLevel.Fastest);
                    await using var target = entry.Open();
                    await using var input = await OpenEvidenceSourceAsync(source, token);
                    var (actualChecksum, actualSize) = await CopyAndHashAsync(input, target, MaxEvidencePackageSourceSize - copiedSize,
                        token, () => RequireSignaturePolicy(signaturePolicy));
                    if (!string.Equals(actualChecksum, source.ExpectedChecksum, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Контрольная сумма файла {source.EntryName} не совпадает с зафиксированной версией.");
                    if (actualSize != source.SizeBytes)
                        throw new InvalidOperationException($"Размер файла {source.EntryName} не совпадает с зафиксированной версией.");
                    copiedSize += actualSize;
                }
                RequireSignaturePolicy(signaturePolicy);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, EvidenceJsonOptions, token);
            }
            if (stageTicket is not null)
            {
                completedCheckpoint = await _stageCapacity!.WriteCheckpointedFileAsync(stageTicket,
                    async (output, initial, token) =>
                    {
                        initialCheckpoint = initial;
                        packageCreated = true;
                        await WriteArchiveAsync(output, token);
                    }, ct);
                completedFileIdentity = completedCheckpoint.FileIdentity;
                completedSha256 = completedCheckpoint.PlannedSha256;
                completedBytes = completedCheckpoint.PlannedBytes;
                packageRootIdentity = stageTicket.RootIdentity;
            }
            else
            {
                await using var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
                packageCreated = true;
                var originalIdentity = StorageStageFileIdentity.Observe(output.SafeFileHandle);
                async Task<bool> CaptureOwnedBytesAsync(CancellationToken token)
                {
                    if (originalIdentity is null) return false;
                    await output.FlushAsync(token);
                    var size = output.Length;
                    output.Position = 0;
                    var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(output, token)).ToLowerInvariant();
                    if (output.Length != size || StorageStageFileIdentity.Observe(output.SafeFileHandle) != originalIdentity)
                        return false;
                    completedFileIdentity = originalIdentity;
                    completedBytes = size;
                    completedSha256 = sha256;
                    return true;
                }
                try
                {
                    await WriteArchiveAsync(output, ct);
                    if (!await CaptureOwnedBytesAsync(ct))
                        throw new InvalidOperationException("Не удалось подтвердить записанный доказательный архив.");
                }
                catch
                {
                    // Observe only the original retained descriptor. An unknown
                    // failed write is never cleaned through a guessed path.
                    if (packageRootIdentity is not null && StorageStageRootIdentity.Observe(_storageRoot) == packageRootIdentity)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await CaptureOwnedBytesAsync(cleanup.Token); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                        {
                            _logger?.LogWarning(error, "Could not verify failed evidence archive bytes: {PackagePath}", packagePath);
                        }
                    }
                    throw;
                }
            }
            RequireSignaturePolicy(signaturePolicy);
            if (stageTicket is not null)
            {
                stageTicket = await _stageCapacity!.ConfirmPhysicalSizeAsync(stageTicket, new FileInfo(packagePath).Length, ct);
                await _stageCapacity.SettlePhysicalAsync(stageTicket, ct);
            }
            RequireSignaturePolicy(signaturePolicy);
            return new ElectronicSignatureEvidencePackage(packagePath,
                $"electronic-signature-{envelope.DocumentType}-{envelope.DocumentId}-v{envelope.DocumentVersion}.zip",
                _storageRoot, packageRootIdentity, completedFileIdentity, completedBytes, completedSha256!,
                () => _logger?.LogWarning("Retained evidence archive after protected cleanup could not be confirmed: {PackagePath}", packagePath));
        }
        catch
        {
            if (stageTicket is not null)
            {
                // Never delete an unknown prefix after a failed checkpoint.
                // Keep the original source exception and pending durable debt.
                var known = completedCheckpoint ?? initialCheckpoint;
                if (packageCreated && known is not null
                    && StorageStageRootIdentity.Observe(_storageRoot) == stageTicket.RootIdentity)
                    await VerifiedOriginalReadFile.TryDeleteAsync(packagePath, known.FileIdentity, known.PlannedBytes, known.PlannedSha256);
            }
            else if (packageCreated)
                await TryCleanupSignatureTemporaryFileAsync(packagePath, _storageRoot, packageRootIdentity,
                    completedFileIdentity, completedBytes, completedSha256);
            throw;
        }
        finally
        {
            try
            {
                if (stageTicket is not null && !File.Exists(packagePath))
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _stageCapacity!.ReleaseAsync(stageTicket, cleanup.Token);
                    }
                    catch (Exception exception)
                    {
                        _logger?.LogWarning(exception, "Could not release evidence archive stage capacity {ReservationKey}", stageTicket.Key);
                    }
                }
            }
            finally
            {
                if (stageTicket is not null) await stageTicket.CloseWriterAsync();
            }
        }
    }

    private static Task<bool> TryCleanupSignatureTemporaryFileAsync(string path, string rootPath, string? rootIdentity,
        string? fileIdentity, long size, string? sha256)
    {
        if (rootIdentity is null || sha256 is null || StorageStageRootIdentity.Observe(rootPath) != rootIdentity)
            return Task.FromResult(false);
        return VerifiedOriginalReadFile.TryDeleteAsync(path, fileIdentity, size, sha256);
    }

    private static long EvidenceEntryUpperBound(string name, long size)
    {
        // Conservative raw-deflate fixed/stored bounds, without relying on
        // runtime-specific window/memory defaults. The physical write guard
        // stays authoritative if a codec changes. Headers/names are separate.
        var fixedBound = checked(size + (size >> 3) + (size >> 8) + (size >> 9) + 64);
        var storedBound = checked(size + (size >> 5) + (size >> 7) + (size >> 11) + 64);
        return checked(Math.Max(fixedBound, storedBound) + 256 + 2L * Encoding.UTF8.GetByteCount(name));
    }

    private void Validate(ElectronicSignaturePreparation request, Stream pdf, string fileName)
    {
        if (!_configuration.GetValue("ElectronicSignature:PreparationEnabled", true))
            throw new InvalidOperationException("Подготовка электронной подписи отключена администратором.");
        if (!DocumentTypes.Contains(request.DocumentType.Trim().ToLowerInvariant()))
            throw new InvalidOperationException("Электронная подпись доступна только для счетов, актов и договоров.");
        if (!SignatureTypes.Contains(request.SignatureType.Trim().ToLowerInvariant()))
            throw new InvalidOperationException("Неизвестный вид электронной подписи.");
        if (!SigningOrders.Contains(request.SigningOrder.Trim().ToLowerInvariant()))
            throw new InvalidOperationException("Неизвестный порядок подписания.");
        if (!ProviderNames.Contains(request.Provider.Trim()))
            throw new InvalidOperationException("Неизвестный провайдер электронной подписи.");
        if (request.DocumentId <= 0 || request.DocumentVersion <= 0 || request.CreatedByUserId <= 0)
            throw new InvalidOperationException("Документ, версия и автор обязательны.");
        if (request.ArchiveRetentionYears is not (5 or 10 or 75))
            throw new InvalidOperationException("Срок архива должен составлять 5, 10 или 75 лет.");
        if (request.Signers.Count == 0 || request.Signers.Count > 20)
            throw new InvalidOperationException("Укажите от одного до двадцати подписантов.");
        if (request.Signers.Any(item => string.IsNullOrWhiteSpace(item.DisplayName) || string.IsNullOrWhiteSpace(item.Contact)))
            throw new InvalidOperationException("Для каждого подписанта обязательны имя и контакт.");
        if (request.Signers.Any(item => item.DisplayName.Trim().Length > 256 || item.Contact.Trim().Length > 254))
            throw new InvalidOperationException("Имя или контакт подписанта слишком длинные.");
        if (request.Signers.Any(item => !PartyTypes.Contains(item.PartyType.Trim().ToLowerInvariant())))
            throw new InvalidOperationException("Подписантом может быть только клиент или явно назначенный представитель.");
        if (request.Signers.Any(item => item.RequiresMachineReadablePowerOfAttorney
            && (string.IsNullOrWhiteSpace(item.PowerOfAttorneyReference) || item.PowerOfAttorneyReference.Trim().Length > 256)))
            throw new InvalidOperationException("Для подписанта с МЧД укажите номер или ссылку длиной до 256 символов.");
        if (!pdf.CanRead || string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("PDF-документ обязателен.");
    }

    private static string BuildIdempotencyKey(ElectronicSignaturePreparation request, string checksum)
    {
        var signerKey = string.Join('|', request.Signers.Select(item => item.Contact.Trim().ToLowerInvariant()));
        var raw = $"{request.DocumentType}:{request.DocumentId}:{request.DocumentVersion}:{request.SignatureType}:{request.SigningOrder}:{request.Provider}:{checksum}:{signerKey}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string MaskContact(string contact)
    {
        var value = contact.Trim();
        var at = value.IndexOf('@');
        if (at > 1)
            return $"{value[0]}***{value[(at - 1)..]}";
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? $"***{digits[^4..]}" : "***";
    }

    private static async Task<int> ReadAtMostAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private string EnsureStoragePath(string path)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_storageRoot, path));
        if (IsWithinStorageRoot(fullPath, _storageRoot))
        {
            return fullPath;
        }

        if (IsWithinStorageRoot(fullPath, _legacyStorageRoot))
        {
            var relativePath = Path.GetRelativePath(_legacyStorageRoot, fullPath);
            var migratedPath = Path.GetFullPath(Path.Combine(_storageRoot, relativePath));
            return File.Exists(migratedPath) ? migratedPath : fullPath;
        }

        throw new InvalidOperationException("Файл доказательного пакета находится вне защищённого хранилища.");
    }

    private static bool IsWithinStorageRoot(string fullPath, string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullPath, normalizedRoot, comparison)
            || fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string SanitizeEntryName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "artifact.bin" : safe[..Math.Min(safe.Length, 120)];
    }

    private sealed record EvidenceSource(string EntryName, string FullPath, string ExpectedChecksum,
        string ArtifactType, string Source, long Id, string FileName, string ContentType, long SizeBytes);

    private async Task<Stream> OpenEvidenceSourceAsync(EvidenceSource source, CancellationToken ct)
    {
        if (_productStorageReader is not null)
        {
            var result = await _productStorageReader.OpenReadAsync(source.Source, source.Id,
                source.FileName, source.ContentType, ct);
            if (result.Status != ProductStorageReadStatus.Legacy)
            {
                var file = result.File;
                if (result.Status == ProductStorageReadStatus.Available && file is not null
                    && file.SizeBytes == source.SizeBytes
                    && !string.IsNullOrWhiteSpace(file.Sha256)
                    && string.Equals(file.Sha256, source.ExpectedChecksum, StringComparison.OrdinalIgnoreCase))
                    return file.Content;
                if (file is not null) await file.Content.DisposeAsync();
                throw new InvalidOperationException($"Проверенная копия файла {source.EntryName} недоступна.");
            }
        }
        var path = EnsureStoragePath(source.FullPath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Файл доказательного пакета не найден: {source.EntryName}.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    private static async Task<(string Checksum, long SizeBytes)> CopyAndHashAsync(Stream input, Stream output, long limit, CancellationToken ct,
        Action? validatePolicy = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            validatePolicy?.Invoke();
            if (read > limit - size)
                throw new InvalidOperationException("Общий размер файлов доказательного пакета превышает 100 МБ.");
            size += read;
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
    }

}
