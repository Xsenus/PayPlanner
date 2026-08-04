using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public interface IExternalWorkOrderApprovalService
{
    Task<ExternalWorkOrderApprovalStatusDto?> GetLatestStatusAsync(int caseId, CancellationToken ct);
    Task<ExternalWorkOrderApprovalStatusDto> CreateAsync(
        int caseId,
        CreateExternalWorkOrderApprovalRequest request,
        int? actorUserId,
        string actorName,
        CancellationToken ct);
    Task<bool> RevokeAsync(int caseId, int? actorUserId, string actorName, CancellationToken ct);
    Task<PublicWorkOrderApprovalDto> OpenAsync(Guid publicId, string token, CancellationToken ct);
    Task<PublicWorkOrderApprovalDecisionDto> DecideAsync(
        Guid publicId,
        PublicWorkOrderApprovalDecisionRequest request,
        string? remoteIp,
        string? userAgent,
        CancellationToken ct);
    Task<PublicWorkOrderAttachmentGrantDto> CreateAttachmentGrantAsync(
        Guid publicId,
        Guid attachmentPublicId,
        string token,
        CancellationToken ct);
    Task<PublicWorkOrderAttachmentDownload> DownloadAttachmentAsync(
        Guid publicId,
        Guid attachmentPublicId,
        string grantToken,
        CancellationToken ct);
}

public sealed class ExternalWorkOrderApprovalService : IExternalWorkOrderApprovalService
{
    private const int LifetimeHours = 72;
    private const int MaxAttempts = 5;
    private const int MaxAttachmentCount = 10;
    private const long MaxAttachmentSizeBytes = 25L * 1024 * 1024;
    private static readonly TimeSpan AttachmentGrantLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> FinalStatuses = new(StringComparer.Ordinal)
    {
        "approved", "revision_requested",
    };

    private readonly PaymentContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;
    private readonly IExternalWorkOrderApprovalNotifier? _notifier;
    private readonly ILogger<ExternalWorkOrderApprovalService>? _logger;
    private readonly ICaseDocumentStorage? _documentStorage;
    private readonly ITimeLimitedDataProtector? _attachmentGrantProtector;
    private readonly IMaxOutboxService? _maxOutbox;

    public ExternalWorkOrderApprovalService(
        PaymentContext db,
        IEmailService email,
        IConfiguration configuration,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
        IExternalWorkOrderApprovalNotifier? notifier = null,
        ILogger<ExternalWorkOrderApprovalService>? logger = null,
        ICaseDocumentStorage? documentStorage = null,
        IDataProtectionProvider? dataProtectionProvider = null,
        IMaxOutboxService? maxOutbox = null)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _featureFlags = featureFlags;
        _notifier = notifier;
        _logger = logger;
        _documentStorage = documentStorage;
        _attachmentGrantProtector = dataProtectionProvider?
            .CreateProtector("PayPlanner.ExternalWorkOrderApproval.AttachmentGrant.v1")
            .ToTimeLimitedDataProtector();
        _maxOutbox = maxOutbox;
    }

    public async Task<ExternalWorkOrderApprovalStatusDto?> GetLatestStatusAsync(int caseId, CancellationToken ct)
    {
        EnsureEnabled();
        return await _db.ExternalWorkOrderApprovalRequests
            .AsNoTracking()
            .Where(item => item.WorkOrder.ClientCaseId == caseId)
            .OrderByDescending(item => item.Version)
            .Select(item => new ExternalWorkOrderApprovalStatusDto
            {
                PublicId = item.PublicId,
                Version = item.Version,
                Status = item.Status,
                RecipientName = item.RecipientName,
                RecipientEmail = item.RecipientEmail,
                RecipientType = item.RecipientType,
                CreatedAt = item.CreatedAt,
                ExpiresAt = item.ExpiresAt,
                OpenedAt = item.OpenedAt,
                LastSentAt = item.LastSentAt,
                Decision = item.Decision,
                DecisionComment = item.DecisionComment,
                DecisionAt = item.DecisionAt,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ExternalWorkOrderApprovalStatusDto> CreateAsync(
        int caseId,
        CreateExternalWorkOrderApprovalRequest request,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        EnsureEnabled();
        var recipientName = NormalizeRequired(request.RecipientName, 256, "Укажите имя получателя.");
        var recipientEmail = NormalizeEmail(request.RecipientEmail);
        var recipientType = NormalizeRecipientType(request.RecipientType);
        var deliveryChannel = NormalizeDeliveryChannel(request.DeliveryChannel);

        var aggregate = await _db.ClientCaseWorkOrders
            .Where(item => item.ClientCaseId == caseId)
            .Select(item => new
            {
                WorkOrder = item,
                ClientCase = item.ClientCase,
                CaseTitle = item.ClientCase.Title,
                CaseNumber = item.ClientCase.CaseNumber,
                ClientName = item.ClientCase.Client != null
                    ? item.ClientCase.Client.Name
                    : item.ClientCase.LegalEntity != null
                        ? item.ClientCase.LegalEntity.ShortName
                        : string.Empty,
                Items = item.Items
                    .OrderBy(row => row.SortOrder)
                    .ThenBy(row => row.Id)
                    .Select(row => new ExternalWorkOrderSnapshotItemDto
                    {
                        ServiceName = row.ServiceName,
                        Description = row.Description,
                        DueDate = row.DueDate,
                        Quantity = row.Quantity,
                        UnitPrice = row.UnitPrice,
                        TotalAmount = row.TotalAmount,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new ExternalWorkOrderApprovalException(
                "work_order_not_found",
                "Заказ-наряд по делу не найден.");

        if (aggregate.Items.Count == 0 || aggregate.WorkOrder.TotalAmount <= 0)
        {
            throw new ExternalWorkOrderApprovalException(
                "work_order_empty",
                "Добавьте услуги с ненулевой стоимостью перед отправкой клиенту.");
        }

        int? maxPortalAccountId = null;
        if (deliveryChannel == "email_and_max")
        {
            if (_maxOutbox is null)
                throw new ExternalWorkOrderApprovalException("delivery_unavailable", "Доставка через MAX не подключена.");
            var candidates = await _db.ClientPortalAccounts.AsNoTracking()
                .Where(item => item.Email == recipientEmail && item.IsActive && item.RevokedAt == null)
                .Where(item => item.CaseGrants.Any(grant => grant.ClientCaseId == caseId
                    && grant.RevokedAt == null
                    && grant.CanViewApprovals))
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .Take(2)
                .ToListAsync(ct);
            if (candidates.Count != 1)
                throw new ExternalWorkOrderApprovalException(
                    "max_recipient_unavailable",
                    "Для MAX нужен ровно один активный аккаунт кабинета с этим email и правом просмотра согласований данного дела.");
            maxPortalAccountId = candidates[0];
        }

        var now = DateTime.UtcNow;
        var attachments = await BuildAttachmentsAsync(caseId, request.DocumentIds, ct);
        var previousWorkOrderStatus = aggregate.WorkOrder.Status;
        var previousApprovalRequestedAt = aggregate.WorkOrder.ApprovalRequestedAt;
        var previousApprovalRequestedByUserId = aggregate.WorkOrder.ApprovalRequestedByUserId;
        var previousApprovalRequestedByUserName = aggregate.WorkOrder.ApprovalRequestedByUserName;
        var activeRequests = await _db.ExternalWorkOrderApprovalRequests
            .Where(item => item.ClientCaseWorkOrderId == aggregate.WorkOrder.Id
                && (item.Status == "pending" || item.Status == "opened"))
            .ToListAsync(ct);
        foreach (var active in activeRequests)
        {
            Revoke(active, actorUserId, actorName, "Создана новая версия запроса.");
        }

        var version = await _db.ExternalWorkOrderApprovalRequests
            .Where(item => item.ClientCaseWorkOrderId == aggregate.WorkOrder.Id)
            .Select(item => (int?)item.Version)
            .MaxAsync(ct) ?? 0;

        var snapshot = new ExternalWorkOrderSnapshotDto
        {
            OrganizationName = await ResolveOrganizationNameAsync(ct),
            ClientName = aggregate.ClientName,
            CaseTitle = aggregate.CaseTitle,
            CaseNumber = aggregate.CaseNumber,
            Version = version + 1,
            Title = aggregate.WorkOrder.Title,
            Currency = aggregate.WorkOrder.Currency,
            TotalAmount = aggregate.WorkOrder.TotalAmount,
            Items = aggregate.Items,
            Attachments = attachments.Select(item => new ExternalWorkOrderSnapshotAttachmentDto
            {
                PublicId = item.PublicId,
                FileName = item.FileName,
                ContentType = item.ContentType,
                SizeBytes = item.SizeBytes,
            }).ToArray(),
        };
        var snapshotJson = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);
        var accessToken = GenerateToken(32);
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        var entity = new ExternalWorkOrderApprovalRequest
        {
            PublicId = Guid.NewGuid(),
            ClientCaseWorkOrderId = aggregate.WorkOrder.Id,
            Version = snapshot.Version,
            Status = "pending",
            SnapshotJson = snapshotJson,
            SnapshotHash = Hash(snapshotJson),
            AccessTokenHash = Hash(accessToken),
            OtpHash = Hash($"{accessToken}:{otp}"),
            RecipientName = recipientName,
            RecipientEmail = recipientEmail,
            RecipientType = recipientType,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            CreatedByUserName = Truncate(actorName, 256),
            ExpiresAt = now.AddHours(LifetimeHours),
            LastSentAt = now,
            MaxAttempts = MaxAttempts,
            Attachments = attachments,
        };
        _db.ExternalWorkOrderApprovalRequests.Add(entity);
        aggregate.WorkOrder.ApprovalRequestedAt = now;
        aggregate.WorkOrder.ApprovalRequestedByUserId = actorUserId;
        aggregate.WorkOrder.ApprovalRequestedByUserName = Truncate(actorName, 256);
        aggregate.WorkOrder.Status = "pending_approval";
        AppendCaseEvent(
            aggregate.ClientCase,
            $"Заказ-наряд «{aggregate.WorkOrder.Title}» версии {snapshot.Version} отправлен на внешнее согласование: {recipientName} ({MaskEmail(recipientEmail)}).");
        _db.SetAuditReason($"Заказ-наряд дела #{caseId} отправлен на внешнее согласование.");
        await _db.SaveChangesAsync(ct);

        var publicUrl = BuildPublicUrl(entity.PublicId, accessToken);
        try
        {
            await _email.SendEmailAsync(
                recipientEmail,
                $"Согласование заказ-наряда: {aggregate.WorkOrder.Title}",
                BuildEmailBody(recipientName, snapshot, publicUrl, otp, entity.ExpiresAt),
                isHtml: true,
                cancellationToken: ct);
            if (maxPortalAccountId.HasValue)
            {
                await _maxOutbox!.QueueAsync(new MaxQueueRequest(
                    null,
                    MaxMessengerScenarios.Confirmations,
                    $"work-order-approval:{entity.Id}:v{entity.Version}",
                    "В CRM подготовлен запрос на согласование. Решение принимается только на защищённой странице.",
                    "Открыть согласование",
                    publicUrl,
                    RecipientPortalAccountId: maxPortalAccountId), ct);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Revoke(entity, actorUserId, actorName, "Доставка email не выполнена.");
            entity.Status = "delivery_failed";
            aggregate.WorkOrder.Status = previousWorkOrderStatus;
            aggregate.WorkOrder.ApprovalRequestedAt = previousApprovalRequestedAt;
            aggregate.WorkOrder.ApprovalRequestedByUserId = previousApprovalRequestedByUserId;
            aggregate.WorkOrder.ApprovalRequestedByUserName = previousApprovalRequestedByUserName;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new ExternalWorkOrderApprovalException(
                "delivery_failed",
                deliveryChannel == "email_and_max"
                    ? "Не удалось доставить email и уведомление MAX. Ссылка автоматически отключена; проверьте оба канала и повторите отправку."
                    : "Не удалось отправить письмо. Ссылка автоматически отключена; проверьте настройки email и повторите отправку.");
        }

        return MapStatus(entity);
    }

    public async Task<bool> RevokeAsync(int caseId, int? actorUserId, string actorName, CancellationToken ct)
    {
        EnsureEnabled();
        var active = await _db.ExternalWorkOrderApprovalRequests
            .Where(item => item.WorkOrder.ClientCaseId == caseId
                && (item.Status == "pending" || item.Status == "opened"))
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(ct);
        if (active is null)
        {
            return false;
        }

        Revoke(active, actorUserId, actorName, "Запрос отозван сотрудником CRM.");
        _db.SetAuditReason($"Внешнее согласование заказ-наряда дела #{caseId} отозвано.");
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PublicWorkOrderApprovalDto> OpenAsync(Guid publicId, string token, CancellationToken ct)
    {
        EnsureEnabled(publicSurface: true);
        var entity = await LoadPublicRequestAsync(publicId, token, ct);
        await EnsureUsableAsync(entity, ct);
        VerifySnapshot(entity);

        if (entity.Status == "pending")
        {
            entity.Status = "opened";
            entity.OpenedAt ??= DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new PublicWorkOrderApprovalDto
        {
            PublicId = entity.PublicId,
            Status = entity.Status,
            ExpiresAt = entity.ExpiresAt,
            RecipientHint = MaskEmail(entity.RecipientEmail),
            Snapshot = DeserializeSnapshot(entity),
            Decision = entity.Decision,
            DecisionComment = entity.DecisionComment,
            DecisionAt = entity.DecisionAt,
        };
    }

    public async Task<PublicWorkOrderApprovalDecisionDto> DecideAsync(
        Guid publicId,
        PublicWorkOrderApprovalDecisionRequest request,
        string? remoteIp,
        string? userAgent,
        CancellationToken ct)
    {
        EnsureEnabled(publicSurface: true);
        var token = request.Token?.Trim() ?? string.Empty;
        var entity = await LoadPublicRequestAsync(publicId, token, ct);
        await EnsureUsableAsync(entity, ct);
        VerifySnapshot(entity);

        var decision = NormalizeDecision(request.Decision);
        if (FinalStatuses.Contains(entity.Status))
        {
            if (string.Equals(entity.Decision, decision, StringComparison.Ordinal))
            {
                return MapDecision(entity);
            }

            throw new ExternalWorkOrderApprovalException(
                "already_decided",
                "По этой версии заказ-наряда уже принято решение.");
        }

        var otp = request.Otp?.Trim() ?? string.Empty;
        if (!FixedTimeEquals(entity.OtpHash, Hash($"{token}:{otp}")))
        {
            entity.FailedAttempts++;
            if (entity.FailedAttempts >= entity.MaxAttempts)
            {
                entity.Status = "locked";
            }
            await _db.SaveChangesAsync(ct);
            throw new ExternalWorkOrderApprovalException(
                "invalid_confirmation",
                entity.Status == "locked"
                    ? "Ссылка заблокирована после превышения числа попыток."
                    : "Неверный код подтверждения.");
        }

        var now = DateTime.UtcNow;
        entity.Status = decision == "approved" ? "approved" : "revision_requested";
        entity.Decision = decision;
        entity.DecisionComment = Truncate(request.Comment?.Trim() ?? string.Empty, 2000);
        entity.DecisionAt = now;
        entity.DecisionIpHash = Hash($"{entity.PublicId:N}:{remoteIp?.Trim()}");
        entity.DecisionUserAgent = Truncate(userAgent?.Trim() ?? string.Empty, 512);

        var workOrder = await _db.ClientCaseWorkOrders
            .Include(item => item.ClientCase)
            .FirstAsync(item => item.Id == entity.ClientCaseWorkOrderId, ct);
        workOrder.Status = decision == "approved" ? "approved" : "revision_required";
        workOrder.ApprovalDecisionAt = now;
        workOrder.ApprovalDecisionByUserId = null;
        workOrder.ApprovalDecisionByUserName = entity.RecipientName;
        workOrder.ApprovalComment = Truncate(entity.DecisionComment, 1000);
        workOrder.UpdatedAt = now;
        AppendCaseEvent(
            workOrder.ClientCase,
            decision == "approved"
                ? $"Клиент согласовал заказ-наряд «{workOrder.Title}» версии {entity.Version}."
                : $"Клиент вернул заказ-наряд «{workOrder.Title}» версии {entity.Version} с замечаниями.");
        _db.SetAuditReason($"Получено внешнее решение по заказ-наряду дела #{workOrder.ClientCaseId}.");
        await _db.SaveChangesAsync(ct);
        if (_notifier is not null)
        {
            try
            {
                await _notifier.NotifyDecisionAsync(entity, workOrder, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "External work order decision notification failed for request {RequestId}.", entity.Id);
            }
        }
        return MapDecision(entity);
    }

    public async Task<PublicWorkOrderAttachmentGrantDto> CreateAttachmentGrantAsync(
        Guid publicId,
        Guid attachmentPublicId,
        string token,
        CancellationToken ct)
    {
        EnsureEnabled(publicSurface: true);
        EnsureAttachmentSupport();
        var request = await LoadPublicRequestAsync(publicId, token.Trim(), ct);
        EnsureAttachmentWindow(request);
        VerifySnapshot(request);
        var exists = await _db.ExternalWorkOrderApprovalAttachments
            .AsNoTracking()
            .AnyAsync(item => item.ExternalWorkOrderApprovalRequestId == request.Id
                && item.PublicId == attachmentPublicId, ct);
        if (!exists)
        {
            throw new ExternalWorkOrderApprovalException("attachment_unavailable", "Вложение недоступно.");
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(AttachmentGrantLifetime);
        var payload = JsonSerializer.Serialize(new AttachmentGrantPayload(
            publicId,
            attachmentPublicId,
            request.SnapshotHash));
        return new PublicWorkOrderAttachmentGrantDto
        {
            GrantToken = _attachmentGrantProtector!.Protect(payload, AttachmentGrantLifetime),
            ExpiresAt = expiresAt.UtcDateTime,
        };
    }

    public async Task<PublicWorkOrderAttachmentDownload> DownloadAttachmentAsync(
        Guid publicId,
        Guid attachmentPublicId,
        string grantToken,
        CancellationToken ct)
    {
        EnsureEnabled(publicSurface: true);
        EnsureAttachmentSupport();
        AttachmentGrantPayload payload;
        try
        {
            var json = _attachmentGrantProtector!.Unprotect(grantToken.Trim(), out _);
            payload = JsonSerializer.Deserialize<AttachmentGrantPayload>(json)
                ?? throw new CryptographicException("Attachment grant payload is empty.");
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new ExternalWorkOrderApprovalException("attachment_unavailable", "Ссылка на вложение недействительна или истекла.");
        }

        if (payload.PublicId != publicId || payload.AttachmentPublicId != attachmentPublicId)
        {
            throw new ExternalWorkOrderApprovalException("attachment_unavailable", "Вложение недоступно.");
        }

        var attachment = await _db.ExternalWorkOrderApprovalAttachments
            .AsNoTracking()
            .Include(item => item.Request)
            .Include(item => item.Document)
            .FirstOrDefaultAsync(item => item.PublicId == attachmentPublicId
                && item.Request.PublicId == publicId, ct)
            ?? throw new ExternalWorkOrderApprovalException("attachment_unavailable", "Вложение недоступно.");
        EnsureAttachmentWindow(attachment.Request);
        VerifySnapshot(attachment.Request);
        if (!FixedTimeEquals(payload.SnapshotHash, attachment.Request.SnapshotHash)
            || !_documentStorage!.Exists(attachment.Document.StoragePath))
        {
            throw new ExternalWorkOrderApprovalException("attachment_unavailable", "Вложение недоступно.");
        }

        var path = _documentStorage.ResolvePath(attachment.Document.StoragePath);
        var currentHash = await HashFileAsync(path, ct);
        if (!FixedTimeEquals(currentHash, attachment.Sha256))
        {
            throw new ExternalWorkOrderApprovalException("attachment_integrity_error", "Не удалось проверить неизменность вложения.");
        }

        return new PublicWorkOrderAttachmentDownload(path, attachment.FileName, attachment.ContentType);
    }

    private async Task<List<ExternalWorkOrderApprovalAttachment>> BuildAttachmentsAsync(
        int caseId,
        IReadOnlyCollection<int>? requestedIds,
        CancellationToken ct)
    {
        var ids = (requestedIds ?? Array.Empty<int>())
            .Where(static id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        if (ids.Length > MaxAttachmentCount)
        {
            throw new ExternalWorkOrderApprovalException("validation_error", $"Можно приложить не более {MaxAttachmentCount} документов.");
        }
        EnsureAttachmentSupport();

        var documents = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => item.ClientCaseId == caseId && ids.Contains(item.Id))
            .ToListAsync(ct);
        if (documents.Count != ids.Length)
        {
            throw new ExternalWorkOrderApprovalException("validation_error", "Один или несколько документов не относятся к выбранному делу.");
        }

        var result = new List<ExternalWorkOrderApprovalAttachment>(documents.Count);
        foreach (var document in documents.OrderBy(item => item.OriginalFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (document.SizeBytes <= 0 || document.SizeBytes > MaxAttachmentSizeBytes
                || !IsAllowedAttachment(document.ContentType, document.OriginalFileName)
                || !_documentStorage!.Exists(document.StoragePath))
            {
                throw new ExternalWorkOrderApprovalException("validation_error", $"Документ «{document.OriginalFileName}» нельзя передать клиенту.");
            }

            var path = _documentStorage.ResolvePath(document.StoragePath);
            result.Add(new ExternalWorkOrderApprovalAttachment
            {
                PublicId = Guid.NewGuid(),
                ClientCaseDocumentId = document.Id,
                FileName = Truncate(document.OriginalFileName, 260),
                ContentType = Truncate(document.ContentType, 200),
                SizeBytes = document.SizeBytes,
                Sha256 = await HashFileAsync(path, ct),
            });
        }
        return result;
    }

    private static bool IsAllowedAttachment(string? contentType, string fileName)
    {
        var normalizedType = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return (normalizedType, extension) switch
        {
            ("application/pdf", ".pdf") => true,
            ("image/jpeg", ".jpg" or ".jpeg") => true,
            ("image/png", ".png") => true,
            ("text/plain", ".txt") => true,
            ("application/msword", ".doc") => true,
            ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx") => true,
            ("application/vnd.ms-excel", ".xls") => true,
            ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx") => true,
            _ => false,
        };
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void EnsureAttachmentSupport()
    {
        if (_documentStorage is null || _attachmentGrantProtector is null)
        {
            throw new ExternalWorkOrderApprovalException("attachment_unavailable", "Вложения временно недоступны.");
        }
    }

    private static void EnsureAttachmentWindow(ExternalWorkOrderApprovalRequest request)
    {
        if (request.ExpiresAt <= DateTime.UtcNow || request.Status is "revoked" or "locked" or "expired" or "delivery_failed")
        {
            throw new ExternalWorkOrderApprovalException("attachment_unavailable", "Срок доступа к вложению истёк.");
        }
    }

    private async Task<ExternalWorkOrderApprovalRequest> LoadPublicRequestAsync(Guid publicId, string token, CancellationToken ct)
    {
        var entity = await _db.ExternalWorkOrderApprovalRequests
            .FirstOrDefaultAsync(item => item.PublicId == publicId, ct);
        if (entity is null || string.IsNullOrWhiteSpace(token)
            || !FixedTimeEquals(entity.AccessTokenHash, Hash(token)))
        {
            throw new ExternalWorkOrderApprovalException(
                "link_unavailable",
                "Ссылка недействительна или больше недоступна.");
        }
        return entity;
    }

    private void EnsureEnabled(bool publicSurface = false)
    {
        if (!_featureFlags.CurrentValue.Legal.ExternalWorkOrderApproval)
        {
            throw new ExternalWorkOrderApprovalException(
                "feature_disabled",
                publicSurface
                    ? "Ссылка недействительна или больше недоступна."
                    : "Внешнее согласование выключено в настройках сервера.");
        }
    }

    private async Task EnsureUsableAsync(ExternalWorkOrderApprovalRequest entity, CancellationToken ct)
    {
        if (entity.ExpiresAt <= DateTime.UtcNow && !FinalStatuses.Contains(entity.Status))
        {
            entity.Status = "expired";
            await _db.SaveChangesAsync(ct);
            throw new ExternalWorkOrderApprovalException("link_expired", "Срок действия ссылки истёк.");
        }
        if (entity.Status is "revoked" or "locked" or "expired")
        {
            throw new ExternalWorkOrderApprovalException("link_unavailable", "Ссылка недействительна или больше недоступна.");
        }
    }

    private static void VerifySnapshot(ExternalWorkOrderApprovalRequest entity)
    {
        if (!FixedTimeEquals(entity.SnapshotHash, Hash(entity.SnapshotJson)))
        {
            throw new ExternalWorkOrderApprovalException(
                "snapshot_integrity_error",
                "Не удалось проверить неизменность заказ-наряда.");
        }
    }

    private async Task<string> ResolveOrganizationNameAsync(CancellationToken ct)
    {
        var configured = _configuration["App:OrganizationName"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Truncate(configured, 500);
        }

        var rawId = await _db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == "legalEntities.defaultMainCompanyId"
                || item.Key == "legalEntities.mainCompanyId")
            .OrderByDescending(item => item.Key == "legalEntities.defaultMainCompanyId")
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(rawId, out var id) && id > 0
            ? await _db.LegalEntities.AsNoTracking()
                .Where(item => item.Id == id)
                .Select(item => item.FullName ?? item.ShortName)
                .FirstOrDefaultAsync(ct) ?? "Исполнитель"
            : "Исполнитель";
    }

    private string BuildPublicUrl(Guid publicId, string token)
    {
        var baseUrl = (_configuration["App:FrontendUrl"]
            ?? _configuration["Frontend:BaseUrl"]
            ?? _configuration["Frontend:PublicUrl"]
            ?? "http://localhost:5173").TrimEnd('/');
        return $"{baseUrl}/external/work-order-approval/{publicId:D}#token={Uri.EscapeDataString(token)}";
    }

    private static string BuildEmailBody(
        string recipientName,
        ExternalWorkOrderSnapshotDto snapshot,
        string publicUrl,
        string otp,
        DateTime expiresAt)
    {
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#1e293b">
              <h2>Согласование заказ-наряда</h2>
              <p>{WebUtility.HtmlEncode(recipientName)}, для вас подготовлен заказ-наряд
              <strong>{WebUtility.HtmlEncode(snapshot.Title)}</strong> на сумму
              <strong>{snapshot.TotalAmount:0.00} {WebUtility.HtmlEncode(snapshot.Currency)}</strong>.</p>
              <p><a href="{WebUtility.HtmlEncode(publicUrl)}" style="display:inline-block;padding:12px 18px;background:#1d4ed8;color:white;text-decoration:none;border-radius:8px">Открыть заказ-наряд</a></p>
              <p>Код подтверждения решения: <strong style="font-size:20px;letter-spacing:4px">{otp}</strong></p>
              <p style="color:#64748b">Ссылка действует до {expiresAt.ToLocalTime():dd.MM.yyyy HH:mm}. Никому не пересылайте это письмо и код.</p>
            </div>
            """;
    }

    private static void Revoke(
        ExternalWorkOrderApprovalRequest entity,
        int? actorUserId,
        string actorName,
        string reason)
    {
        entity.Status = "revoked";
        entity.RevokedAt = DateTime.UtcNow;
        entity.RevokedByUserId = actorUserId;
        entity.RevokedByUserName = Truncate(actorName, 256);
        entity.RevocationReason = Truncate(reason, 500);
    }

    private static PublicWorkOrderApprovalDecisionDto MapDecision(ExternalWorkOrderApprovalRequest entity)
        => new()
        {
            Status = entity.Status,
            Decision = entity.Decision,
            Comment = entity.DecisionComment,
            DecisionAt = entity.DecisionAt,
        };

    private static ExternalWorkOrderApprovalStatusDto MapStatus(ExternalWorkOrderApprovalRequest entity)
        => new()
        {
            PublicId = entity.PublicId,
            Version = entity.Version,
            Status = entity.Status,
            RecipientName = entity.RecipientName,
            RecipientEmail = entity.RecipientEmail,
            RecipientType = entity.RecipientType,
            CreatedAt = entity.CreatedAt,
            ExpiresAt = entity.ExpiresAt,
            OpenedAt = entity.OpenedAt,
            LastSentAt = entity.LastSentAt,
            Decision = entity.Decision,
            DecisionComment = entity.DecisionComment,
            DecisionAt = entity.DecisionAt,
        };

    private static ExternalWorkOrderSnapshotDto DeserializeSnapshot(ExternalWorkOrderApprovalRequest entity)
        => JsonSerializer.Deserialize<ExternalWorkOrderSnapshotDto>(entity.SnapshotJson, SnapshotJsonOptions)
            ?? throw new ExternalWorkOrderApprovalException("snapshot_invalid", "Не удалось прочитать заказ-наряд.");

    private static string NormalizeRequired(string? value, int maxLength, string error)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ExternalWorkOrderApprovalException("validation_error", error);
        }
        return Truncate(normalized, maxLength);
    }

    private static string NormalizeEmail(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        try
        {
            var address = new MailAddress(normalized ?? string.Empty);
            if (!string.Equals(address.Address, normalized, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException();
            }
            return Truncate(normalized!, 320);
        }
        catch (FormatException)
        {
            throw new ExternalWorkOrderApprovalException("validation_error", "Укажите корректный email получателя.");
        }
    }

    private static string NormalizeRecipientType(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "client" or null or "" => "client",
            "representative" => "representative",
            _ => throw new ExternalWorkOrderApprovalException(
                "validation_error",
                "Получателем может быть только клиент или явно назначенный представитель."),
        };

    private static string NormalizeDeliveryChannel(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "email" => "email",
            "email_and_max" => "email_and_max",
            _ => throw new ExternalWorkOrderApprovalException(
                "validation_error",
                "Доступны варианты доставки: email или email вместе с MAX."),
        };

    private static string NormalizeDecision(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "approved" => "approved",
            "revision_requested" => "revision_requested",
            _ => throw new ExternalWorkOrderApprovalException(
                "validation_error",
                "Выберите согласование или возврат с замечаниями."),
        };

    private static string GenerateToken(int byteLength)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteLength))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string leftHex, string rightHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(leftHex),
                Convert.FromHexString(rightHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1)
        {
            return "***" + (at >= 0 ? email[at..] : string.Empty);
        }
        return $"{email[0]}***{email[(at - 1)..]}";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static void AppendCaseEvent(ClientCase clientCase, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {message}";
        var existing = clientCase.EventHistory?.Trim();
        var next = string.IsNullOrWhiteSpace(existing) ? line : $"{existing}{Environment.NewLine}{line}";
        clientCase.EventHistory = next.Length <= 4000 ? next : next[^4000..];
    }
}

public sealed class ExternalWorkOrderApprovalException : Exception
{
    public ExternalWorkOrderApprovalException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class CreateExternalWorkOrderApprovalRequest
{
    public string? RecipientName { get; set; }
    public string? RecipientEmail { get; set; }
    public string? RecipientType { get; set; }
    public IReadOnlyCollection<int>? DocumentIds { get; set; }
    public string? DeliveryChannel { get; set; } = "email";
}

public sealed class PublicWorkOrderApprovalDecisionRequest
{
    [Required, StringLength(512, MinimumLength = 1)]
    public string? Token { get; set; }

    [Required, StringLength(16, MinimumLength = 4)]
    public string? Otp { get; set; }

    [Required, StringLength(32, MinimumLength = 1)]
    public string? Decision { get; set; }

    [StringLength(2000)]
    public string? Comment { get; set; }
}

public sealed class ExternalWorkOrderApprovalStatusDto
{
    public Guid PublicId { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public string RecipientType { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string DecisionComment { get; set; } = string.Empty;
    public DateTime? DecisionAt { get; set; }
}

public sealed class PublicWorkOrderApprovalDto
{
    public Guid PublicId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string RecipientHint { get; set; } = string.Empty;
    public ExternalWorkOrderSnapshotDto Snapshot { get; set; } = new();
    public string Decision { get; set; } = string.Empty;
    public string DecisionComment { get; set; } = string.Empty;
    public DateTime? DecisionAt { get; set; }
}

public sealed class PublicWorkOrderApprovalDecisionDto
{
    public string Status { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public DateTime? DecisionAt { get; set; }
}

public sealed class ExternalWorkOrderSnapshotDto
{
    public string OrganizationName { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string CaseTitle { get; set; } = string.Empty;
    public string CaseNumber { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Currency { get; set; } = "RUB";
    public decimal TotalAmount { get; set; }
    public IReadOnlyList<ExternalWorkOrderSnapshotItemDto> Items { get; set; } = [];
    public IReadOnlyList<ExternalWorkOrderSnapshotAttachmentDto> Attachments { get; set; } = [];
}

public sealed class ExternalWorkOrderSnapshotAttachmentDto
{
    public Guid PublicId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class PublicWorkOrderAttachmentGrantDto
{
    public string GrantToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public sealed record PublicWorkOrderAttachmentDownload(string Path, string FileName, string ContentType);

internal sealed record AttachmentGrantPayload(Guid PublicId, Guid AttachmentPublicId, string SnapshotHash);

public sealed class ExternalWorkOrderSnapshotItemDto
{
    public string ServiceName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalAmount { get; set; }
}
