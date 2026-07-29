using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public sealed class DisabledSmsNotificationService : ISmsNotificationService
{
    public Task<SmsNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new SmsNotificationSettingsDto
        {
            Enabled = false,
            Ready = false,
            ProviderConfigured = false,
            AdapterConfigured = false,
            ApiTokenSet = false,
            ProviderKey = string.Empty,
            SenderName = string.Empty,
            StatusText = "SMS-уведомления отключены: провайдер и настройки отправителя еще не выбраны."
        });

    public Task<SmsNotificationSettingsDto> UpdateSettingsAsync(
        UpdateSmsNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
        => GetSettingsAsync(cancellationToken);

    public Task<SmsSendResult> SendMessageAsync(
        string phoneNumber,
        string text,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new SmsSendResult(
            false,
            NotificationDeliveryStatus.Skipped,
            "SMS-уведомления отключены: провайдер и настройки отправителя еще не выбраны."));
}
