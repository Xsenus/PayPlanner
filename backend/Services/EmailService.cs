using System.Net;
using System.Net.Mail;

namespace PayPlanner.Api.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;
    private readonly bool _isEnabled;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _isEnabled = _configuration.GetValue<bool>("Email:Enabled", false);
    }

    public async Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
    {
        var subject = "Registration Received - Awaiting Approval";
        var body = $@"
Hello {fullName},

Thank you for registering with PayPlanner!

Your account has been created and is currently awaiting approval from an administrator.
You will receive another email once your account has been approved and you can log in.

If you have any questions, please contact your system administrator.

Best regards,
PayPlanner Team
";

        await SendEmailAsync(toEmail, subject, body);
    }

    public async Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
    {
        var subject = "Account Approved - Welcome to PayPlanner";
        var body = $@"
Hello {fullName},

Great news! Your PayPlanner account has been approved.

You can now log in to the system using your email and password.

Welcome aboard!

Best regards,
PayPlanner Team
";

        await SendEmailAsync(toEmail, subject, body);
    }

    public async Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
    {
        var subject = "Registration Not Approved";
        var body = $@"
Hello {fullName},

We regret to inform you that your PayPlanner registration was not approved.

{(string.IsNullOrEmpty(reason) ? "" : $"Reason: {reason}\n")}
If you believe this is an error or have questions, please contact your system administrator.

Best regards,
PayPlanner Team
";

        await SendEmailAsync(toEmail, subject, body);
    }

    public async Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
    {
        var subject = "New User Registration Pending Approval";
        var body = $@"
Hello Administrator,

A new user has registered and is awaiting approval:

Name: {newUserFullName}
Email: {newUserEmail}

Please log in to the admin panel to review and approve or reject this registration.

Best regards,
PayPlanner System
";

        await SendEmailAsync(adminEmail, subject, body);
    }

    private async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation("Email disabled. Would have sent to {ToEmail}: {Subject}", toEmail, subject);
            return;
        }

        try
        {
            var smtpHost = _configuration["Email:SmtpHost"];
            var smtpPort = _configuration.GetValue<int>("Email:SmtpPort", 587);
            var smtpUsername = _configuration["Email:SmtpUsername"];
            var smtpPassword = _configuration["Email:SmtpPassword"];
            var fromEmail = _configuration["Email:FromEmail"];
            var fromName = _configuration["Email:FromName"] ?? "PayPlanner";

            if (string.IsNullOrEmpty(smtpHost) || string.IsNullOrEmpty(smtpUsername) ||
                string.IsNullOrEmpty(smtpPassword) || string.IsNullOrEmpty(fromEmail))
            {
                _logger.LogWarning("Email configuration incomplete. Cannot send email to {ToEmail}", toEmail);
                return;
            }

            using var client = new SmtpClient(smtpHost, smtpPort)
            {
                Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                EnableSsl = true
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };

            mailMessage.To.Add(toEmail);

            await client.SendMailAsync(mailMessage);
            _logger.LogInformation("Email sent successfully to {ToEmail}: {Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {ToEmail}: {Subject}", toEmail, subject);
        }
    }
}
