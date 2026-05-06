using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public interface IEmailService
{
    Task SendEmailAsync(
        string toEmail,
        string subject,
        string body,
        bool isHtml = false,
        CancellationToken cancellationToken = default,
        EmailSendSettings? settings = null)
        => Task.CompletedTask;

    Task SendRegistrationPendingEmailAsync(string toEmail, string fullName);
    Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName);
    Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason);
    Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail);

    Task SendEmailWithAttachmentAsync(
        string toEmail,
        string subject,
        string body,
        string attachmentFileName,
        byte[] attachmentBytes,
        string attachmentContentType);
}
