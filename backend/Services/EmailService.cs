using System.Net;
using System.Net.Mail;
using System.Text;

namespace PayPlanner.Api.Services
{
    public sealed class EmailService : IEmailService
    {
        private const string DefaultFromName = "PayPlanner";
        private const int DefaultSmtpPort = 587;

        private readonly IConfiguration _configuration;
        private readonly bool _isEnabled;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _isEnabled = _configuration.GetValue<bool>("Email:Enabled", false);
        }

        private static string BuildAdminNewRegistrationBody(string fullName, string email) =>
    $"Hello!\n\nA new user has registered.\nName: {fullName}\nEmail: {email}\n\nBest regards,\nPayPlanner";
        private static string BuildRegistrationApprovedBody(string fullName) =>
    $"Hello, {fullName}!\n\nYour registration has been approved.\nYou can now sign in to PayPlanner.\n\nBest regards,\nPayPlanner team";
        private static string BuildRegistrationPendingBody(string fullName) =>
    $"Hello, {fullName}!\n\nYour registration request has been received and is pending review.\nWe will notify you as soon as the review is completed.\n\nBest regards,\nPayPlanner team";
        private static string BuildRegistrationRejectedBody(string fullName, string? reason)
        {
            var reasonText = string.IsNullOrWhiteSpace(reason)
                ? "No reason provided."
                : reason.Trim();

            return $"Hello, {fullName}!\n\nYour registration was rejected.\nReason: {reasonText}\n\nBest regards,\nPayPlanner team";
        }

        private async Task SendEmailCoreAsync(
            string toEmail,
            string subject,
            string body,
            Action<MailMessage>? configureMessage = null)
        {
            if (!_isEnabled)
                _logger.LogInformation(
                    "Email sending is disabled. Dry-run for {ToEmail}. Subject: {Subject}.",
                    toEmail,
                    subject);
            return;
            if (!TryBuildSmtpSettings(out var settings))
                _logger.LogWarning("SMTP settings are not configured correctly. Skipping email to {ToEmail}.", toEmail);
            return;
            try
            {
                _ = new MailAddress(toEmail);
                using var client = new SmtpClient(settings.Host, settings.Port)
                {
                    Credentials = new NetworkCredential(settings.Username, settings.Password),
                    EnableSsl = settings.EnableSsl,
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(settings.FromEmail, settings.FromName),
                    Subject = subject,
                    SubjectEncoding = Encoding.UTF8,
                    Body = body,
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false,
                };

                message.To.Add(toEmail);
                configureMessage?.Invoke(message);

                await client.SendMailAsync(message);
                _logger.LogInformation("Email was sent to {ToEmail}. Subject: {Subject}.", toEmail, subject);
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "Invalid email address format: {ToEmail}.", toEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {ToEmail}. Subject: {Subject}.", toEmail, subject);
            }
        }

        private bool TryBuildSmtpSettings(out SmtpSettings settings)
        {
            var host = _configuration["Email:SmtpHost"]?.Trim();
            var port = _configuration.GetValue<int?>("Email:SmtpPort") ?? DefaultSmtpPort;
            var username = _configuration["Email:SmtpUsername"]?.Trim();
            var password = _configuration["Email:SmtpPassword"];
            var fromEmail = _configuration["Email:FromEmail"]?.Trim();
            var fromName = string.IsNullOrWhiteSpace(_configuration["Email:FromName"])
                ? DefaultFromName
                : _configuration["Email:FromName"]!.Trim();
            var enableSsl = _configuration.GetValue<bool?>("Email:EnableSsl") ?? true;

            var hasRequiredValues =
                !string.IsNullOrWhiteSpace(host) &&
                !string.IsNullOrWhiteSpace(username) &&
                !string.IsNullOrWhiteSpace(password) &&
                !string.IsNullOrWhiteSpace(fromEmail);

            settings = hasRequiredValues
                ? new SmtpSettings(host!, port, username!, password!, fromEmail!, fromName, enableSsl)
                : default;

            return hasRequiredValues;
        }

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
        {
            return SendEmailCoreAsync(
                adminEmail,
                "New user registration",
                BuildAdminNewRegistrationBody(newUserFullName, newUserEmail));
        }
        public Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string body,
            string attachmentFileName,
            byte[] attachmentBytes,
            string attachmentContentType)
        {
            return SendEmailCoreAsync(toEmail, subject, body, message =>
            {
                if (attachmentBytes.Length == 0) return;
                var stream = new MemoryStream(attachmentBytes);
                var attachment = new Attachment(stream, attachmentFileName, attachmentContentType);
                message.Attachments.Add(attachment);
            });
        }
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
        {
            return SendEmailCoreAsync(
                toEmail,
                "Registration approved",
                BuildRegistrationApprovedBody(fullName));
        }

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
        {
            return SendEmailCoreAsync(
                toEmail,
                "Registration is pending",
                BuildRegistrationPendingBody(fullName));
        }
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
        {
            return SendEmailCoreAsync(
                toEmail,
                "Registration rejected",
                BuildRegistrationRejectedBody(fullName, reason));
        }

        private readonly record struct SmtpSettings(
            string Host,
            int Port,
            string Username,
            string Password,
            string FromEmail,
            string FromName,
            bool EnableSsl);
    }
}
