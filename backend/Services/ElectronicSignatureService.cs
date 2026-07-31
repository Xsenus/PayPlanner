using System.Security.Cryptography;
using System.Text;
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
}

public sealed class ElectronicSignatureService : IElectronicSignatureService
{
    private const long MaxPdfSize = 25L * 1024 * 1024;
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

    public ElectronicSignatureService(
        PaymentContext db,
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        IWebHostEnvironment environment)
    {
        _db = db;
        _configuration = configuration;
        _contactProtector = dataProtectionProvider.CreateProtector("PayPlanner.ElectronicSignature.Contact.v1");
        var configuredRoot = configuration["ElectronicSignature:StorageRoot"];
        _storageRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "storage", "electronic-signatures")
            : configuredRoot);
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
}
