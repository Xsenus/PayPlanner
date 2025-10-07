namespace PayPlanner.Api.Services;

public interface IEmailService
{
    Task SendRegistrationPendingEmailAsync(string toEmail, string fullName);
    Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName);
    Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason);
    Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail);
}
