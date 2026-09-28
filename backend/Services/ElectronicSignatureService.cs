using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

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

public sealed record ElectronicSignatureEvidencePackage(string FullPath, string FileName);

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

    public ElectronicSignatureService(
        PaymentContext db,
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        IWebHostEnvironment environment)
    {
        _db = db;
        _configuration = configuration;
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
        Directory.CreateDirectory(_storageRoot);

        var temporaryPath = Path.Combine(_storageRoot, $".{Guid.NewGuid():N}.tmp");
        string checksum;
        long size;
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var header = new byte[5];
                var headerRead = await ReadAtMostAsync(pdf, header, ct);
                if (headerRead < 5 || !header.AsSpan().SequenceEqual("%PDF-"u8))
                {
                    throw new InvalidOperationException("Разрешены только корректные PDF-документы.");
                }

                await output.WriteAsync(header, ct);
                hash.AppendData(header);
                size = header.Length;
                var buffer = new byte[81920];
                int read;
                while ((read = await pdf.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > MaxPdfSize)
                    {
                        throw new InvalidOperationException("Размер PDF превышает 25 МБ.");
                    }
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                checksum = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var idempotencyKey = BuildIdempotencyKey(request, checksum);
            var existing = await _db.ElectronicSignatureEnvelopes
                .Include(item => item.Signers)
                .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                File.Delete(temporaryPath);
                return existing;
            }

            var publicId = Guid.NewGuid();
            var destinationDirectory = Path.Combine(_storageRoot, publicId.ToString("N"));
            Directory.CreateDirectory(destinationDirectory);
            var destinationPath = Path.Combine(destinationDirectory, "original.pdf");
            File.Move(temporaryPath, destinationPath);

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
            _db.ElectronicSignatureEnvelopes.Add(envelope);
            try
            {
                await _db.SaveChangesAsync(ct);
                return envelope;
            }
            catch
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                throw;
            }
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

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

        var sources = new List<(string EntryName, string FullPath, string ExpectedChecksum, string ArtifactType)>
        {
            ("original/document.pdf", EnsureStoragePath(envelope.SnapshotPath), envelope.ChecksumSha256, "original")
        };
        sources.AddRange(envelope.Artifacts
            .OrderBy(item => item.Id)
            .Select((item, index) => (
                $"artifacts/{index + 1:D3}-{SanitizeEntryName(item.FileName)}",
                EnsureStoragePath(item.StoragePath),
                item.ChecksumSha256,
                item.ArtifactType)));

        long totalSourceSize = 0;
        foreach (var source in sources)
        {
            var info = new FileInfo(source.FullPath);
            if (!info.Exists) throw new InvalidOperationException($"Файл доказательного пакета не найден: {source.EntryName}.");
            totalSourceSize += info.Length;
            if (totalSourceSize > MaxEvidencePackageSourceSize)
                throw new InvalidOperationException("Общий размер файлов доказательного пакета превышает 100 МБ.");
        }

        var exportsDirectory = Path.Combine(_storageRoot, ".exports");
        Directory.CreateDirectory(exportsDirectory);
        CleanupStaleEvidencePackages(exportsDirectory);
        var packagePath = Path.Combine(exportsDirectory, $"{publicId:N}-{Guid.NewGuid():N}.zip");
        try
        {
            var files = new List<object>(sources.Count);
            await using (var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var source in sources)
                {
                    var entry = archive.CreateEntry(source.EntryName, CompressionLevel.Fastest);
                    await using var target = entry.Open();
                    await using var input = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    var actualChecksum = await CopyAndHashAsync(input, target, ct);
                    if (!string.Equals(actualChecksum, source.ExpectedChecksum, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Контрольная сумма файла {source.EntryName} не совпадает с зафиксированной версией.");
                    files.Add(new { path = source.EntryName, type = source.ArtifactType, sizeBytes = input.Length, checksumSha256 = actualChecksum });
                }

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, new
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
                }, EvidenceJsonOptions, ct);
            }

            return new ElectronicSignatureEvidencePackage(
                packagePath,
                $"electronic-signature-{envelope.DocumentType}-{envelope.DocumentId}-v{envelope.DocumentVersion}.zip");
        }
        catch
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            throw;
        }
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

    private static async Task<string> CopyAndHashAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CleanupStaleEvidencePackages(string directory)
    {
        var threshold = DateTime.UtcNow.AddHours(-24);
        foreach (var path in Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly).Take(200))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < threshold) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
