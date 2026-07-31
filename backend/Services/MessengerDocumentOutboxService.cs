using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed record MessengerDocumentProviderRequest(
    int DeliveryId,
    string RecipientAddress,
    string Text,
    string Link,
    string IdempotencyKey,
    int? ClientPortalAccountId = null);

public sealed record MessengerDocumentProviderResult(
    bool Accepted,
    string Status,
    string? ProviderMessageId = null,
    string? Error = null,
    bool IsTransient = false);

public interface IMessengerDocumentProvider
{
    string Channel { get; }
    Task<MessengerDocumentProviderResult> SendAsync(
        MessengerDocumentProviderRequest request,
        CancellationToken ct);
}

public sealed class TelegramMessengerDocumentProvider : IMessengerDocumentProvider
{
    private readonly ITelegramNotificationService _telegram;
    private readonly IConfiguration _configuration;

    public TelegramMessengerDocumentProvider(
        ITelegramNotificationService telegram,
        IConfiguration configuration)
    {
        _telegram = telegram;
        _configuration = configuration;
    }

    public string Channel => MessengerDocumentChannels.Telegram;

    public async Task<MessengerDocumentProviderResult> SendAsync(
        MessengerDocumentProviderRequest request,
        CancellationToken ct)
    {
        if (!_configuration.GetValue("MessengerDocumentDelivery:Providers:Telegram:Enabled", false))
        {
            return new(false, MessengerDocumentDeliveryStatuses.Failed, Error: "Telegram document delivery is disabled.");
        }
        if (!long.TryParse(request.RecipientAddress, out var chatId) || chatId == 0)
        {
            return new(false, MessengerDocumentDeliveryStatuses.Failed, Error: "Telegram recipient id is invalid.");
        }

        var text = string.IsNullOrWhiteSpace(request.Text)
            ? $"Документ из CRM:\n{request.Link}"
            : $"{request.Text.Trim()}\n\n{request.Link}";
        var result = await _telegram.SendMessageAsync(chatId, text, ct);
        return result.Success
            ? new(true, MessengerDocumentDeliveryStatuses.Sent, $"telegram:{request.DeliveryId}")
            : new(false, MessengerDocumentDeliveryStatuses.Failed, Error: result.Error, IsTransient: result.Status == NotificationDeliveryStatus.Failed);
    }
}

public sealed class MaxMessengerDocumentProvider(
    IMaxOutboxService maxOutbox,
    IConfiguration configuration) : IMessengerDocumentProvider
{
    public string Channel => MessengerDocumentChannels.Max;

    public async Task<MessengerDocumentProviderResult> SendAsync(
        MessengerDocumentProviderRequest request,
        CancellationToken ct)
    {
        if (!configuration.GetValue("MessengerDocumentDelivery:Providers:Max:Enabled", false))
            return new(false, MessengerDocumentDeliveryStatuses.Failed, Error: "MAX document delivery is disabled.");
        if (!request.ClientPortalAccountId.HasValue)
            return new(false, MessengerDocumentDeliveryStatuses.Failed, Error: "MAX recipient is not linked to a portal account.");

        try
        {
            var queued = await maxOutbox.QueueAsync(new MaxQueueRequest(
                null,
                MaxMessengerScenarios.Documents,
                $"document-delivery:{request.IdempotencyKey}",
                "В CRM подготовлен документ. Откройте защищённую ссылку, чтобы скачать его.",
                "Открыть документ",
                request.Link,
                RecipientPortalAccountId: request.ClientPortalAccountId), ct);
            return new(true, MessengerDocumentDeliveryStatuses.Queued, $"max-outbox:{queued.PublicId:D}");
        }
        catch (InvalidOperationException ex)
        {
            return new(false, MessengerDocumentDeliveryStatuses.Failed, Error: ex.Message);
        }
    }
}

public sealed class DisabledMessengerDocumentProvider : IMessengerDocumentProvider
{
    public DisabledMessengerDocumentProvider(string channel) => Channel = channel;
    public string Channel { get; }
    public Task<MessengerDocumentProviderResult> SendAsync(MessengerDocumentProviderRequest request, CancellationToken ct)
        => Task.FromResult(new MessengerDocumentProviderResult(
            false,
            MessengerDocumentDeliveryStatuses.Failed,
            Error: $"Provider for {Channel} is not configured."));
}

public interface IMessengerDocumentOutboxProcessor
{
    Task<int> ProcessBatchAsync(CancellationToken ct);
}

public sealed class MessengerDocumentOutboxProcessor : IMessengerDocumentOutboxProcessor
{
    private const int MaxAttempts = 5;
    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;
    private readonly IReadOnlyDictionary<string, IMessengerDocumentProvider> _providers;
    private readonly IDataProtector _protector;
    private readonly ILogger<MessengerDocumentOutboxProcessor> _logger;

    public MessengerDocumentOutboxProcessor(
        PaymentContext db,
        IConfiguration configuration,
        IEnumerable<IMessengerDocumentProvider> providers,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<MessengerDocumentOutboxProcessor> logger)
    {
        _db = db;
        _configuration = configuration;
        _providers = providers.ToDictionary(item => item.Channel, StringComparer.Ordinal);
        _protector = dataProtectionProvider.CreateProtector("PayPlanner.MessengerDocumentDelivery.Address.v1");
        _logger = logger;
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (!_configuration.GetValue("MessengerDocumentDelivery:Enabled", false))
        {
            return 0;
        }
        var publicBaseUrl = _configuration["MessengerDocumentDelivery:PublicBaseUrl"]?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicBaseUrl)
            || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var parsedBase)
            || parsedBase.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("Messenger document outbox is enabled but HTTPS PublicBaseUrl is not configured.");
            return 0;
        }

        var now = DateTime.UtcNow;
        var batchSize = Math.Clamp(_configuration.GetValue("MessengerDocumentDelivery:BatchSize", 20), 1, 100);
        var staleProcessingAt = now.AddMinutes(-10);
        var items = await _db.MessengerDocumentDeliveries
            .Include(item => item.RecipientBinding)
            .Where(item =>
                item.AttemptCount < MaxAttempts
                && item.DownloadCount == 0
                && item.LinkExpiresAt > now
                && ((item.Status == MessengerDocumentDeliveryStatuses.Queued && item.NextAttemptAt <= now)
                    || (item.Status == MessengerDocumentDeliveryStatuses.Failed && item.NextAttemptAt <= now)
                    || (item.Status == MessengerDocumentDeliveryStatuses.Processing && item.UpdatedAt < staleProcessingAt)))
            .OrderBy(item => item.NextAttemptAt)
            .ThenBy(item => item.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            item.Status = MessengerDocumentDeliveryStatuses.Processing;
            item.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);

        foreach (var item in items)
        {
            await ProcessItemAsync(item, publicBaseUrl, ct);
        }
        return items.Count;
    }

    private async Task ProcessItemAsync(MessengerDocumentDelivery item, string baseUrl, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        item.AttemptCount++;
        try
        {
            if (!_providers.TryGetValue(item.Channel, out var provider))
            {
                throw new PermanentDeliveryException("Канал доставки не зарегистрирован.");
            }
            if (!item.RecipientBinding.IsActive
                || item.RecipientBinding.VerifiedAt is null
                || item.RecipientBinding.VerificationStatus != "verified")
            {
                throw new PermanentDeliveryException("Контакт получателя отключён или не подтверждён.");
            }
            if (string.IsNullOrWhiteSpace(item.ProtectedLinkToken))
            {
                throw new PermanentDeliveryException("Защищённый токен ссылки отсутствует.");
            }

            var address = _protector.Unprotect(item.RecipientBinding.ProtectedAddress);
            var token = _protector.Unprotect(item.ProtectedLinkToken);
            var link = $"{baseUrl}/api/public/messenger-document-deliveries/{item.PublicId:D}/download?token={Uri.EscapeDataString(token)}";
            var result = await provider.SendAsync(new(
                item.Id,
                address,
                item.MessagePreview ?? string.Empty,
                link,
                item.IdempotencyKey,
                item.RecipientBinding.ClientPortalAccountId), ct);
            if (!result.Accepted)
            {
                if (!result.IsTransient)
                {
                    throw new PermanentDeliveryException(result.Error ?? "Провайдер отклонил отправку.");
                }
                throw new TransientDeliveryException(result.Error ?? "Временная ошибка провайдера.");
            }

            item.Status = MessengerDocumentDeliveryStatuses.Sent;
            item.ProviderMessageId = result.ProviderMessageId;
            item.SentAt = now;
            item.NextAttemptAt = null;
            item.LastError = null;
            item.ProtectedLinkToken = null;
        }
        catch (PermanentDeliveryException ex)
        {
            MarkFailed(item, ex.Message, now, retry: false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            MarkFailed(item, string.IsNullOrWhiteSpace(ex.Message) ? "Provider request timed out." : ex.Message, now, retry: item.AttemptCount < MaxAttempts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkFailed(item, ex.Message, now, retry: item.AttemptCount < MaxAttempts);
        }
        item.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private static void MarkFailed(MessengerDocumentDelivery item, string error, DateTime now, bool retry)
    {
        item.Status = MessengerDocumentDeliveryStatuses.Failed;
        item.FailedAt = now;
        item.LastError = error.Length <= 2000 ? error : error[..2000];
        item.NextAttemptAt = retry ? now.Add(RetryDelay(item.AttemptCount)) : null;
    }

    private static TimeSpan RetryDelay(int attempt)
        => attempt switch
        {
            <= 1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            4 => TimeSpan.FromHours(1),
            _ => TimeSpan.FromHours(4),
        };

    private sealed class PermanentDeliveryException(string message) : Exception(message);
    private sealed class TransientDeliveryException(string message) : Exception(message);
}

public sealed class MessengerDocumentOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessengerDocumentOutboxWorker> _logger;

    public MessengerDocumentOutboxWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MessengerDocumentOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IMessengerDocumentOutboxProcessor>();
                await processor.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Messenger document outbox iteration failed; it will be retried.");
            }
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
