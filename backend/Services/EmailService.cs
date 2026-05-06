using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services
{
    public sealed class EmailService : IEmailService
    {
        private const string DefaultFromName = "PayPlanner";
        private const int DefaultSmtpPort = 587;

        private readonly IConfiguration _configuration;
        private readonly PaymentContext _context;
        private readonly ILogger<EmailService> _logger;

        public EmailService(
            IConfiguration configuration,
            PaymentContext context,
            ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _context = context;
            _logger = logger;
        }

        private static string BuildAdminNewRegistrationBody(string fullName, string email) =>
    $"Здравствуйте!\n\nВ системе зарегистрировался новый пользователь.\nИмя: {fullName}\nEmail: {email}\n\nPayPlanner";
        private static string BuildRegistrationApprovedBody(string fullName) =>
    $"Здравствуйте, {fullName}!\n\nВаша регистрация одобрена. Теперь можно войти в PayPlanner.\n\nКоманда PayPlanner";
        private static string BuildRegistrationPendingBody(string fullName) =>
    $"Здравствуйте, {fullName}!\n\nЗаявка на регистрацию получена и ожидает проверки администратором. Мы сообщим, когда доступ будет одобрен.\n\nКоманда PayPlanner";
        private static string BuildRegistrationRejectedBody(string fullName, string? reason)
        {
            var reasonText = string.IsNullOrWhiteSpace(reason)
                ? "Причина не указана."
                : reason.Trim();

            return $"Здравствуйте, {fullName}!\n\nЗаявка на регистрацию отклонена.\nПричина: {reasonText}\n\nКоманда PayPlanner";
        }

        private async Task SendEmailCoreAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            Action<MailMessage>? configureMessage = null,
            CancellationToken cancellationToken = default,
            bool throwOnFailure = false,
            EmailSendSettings? overrideSettings = null)
        {
            var settings = overrideSettings is null
                ? await BuildSmtpSettingsAsync(cancellationToken)
                : ConvertSettings(overrideSettings);
            if (!settings.Enabled)
            {
                _logger.LogInformation(
                    "Email sending is disabled. Dry-run for {ToEmail}. Subject: {Subject}.",
                    toEmail,
                    subject);
                return;
            }

            if (!settings.IsReady)
            {
                _logger.LogWarning("SMTP settings are not configured correctly. Skipping email to {ToEmail}.", toEmail);
                return;
            }

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
                    IsBodyHtml = isHtml,
                };

                message.To.Add(toEmail);
                configureMessage?.Invoke(message);

                await client.SendMailAsync(message, cancellationToken);
                _logger.LogInformation("Email was sent to {ToEmail}. Subject: {Subject}.", toEmail, subject);
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "Invalid email address format: {ToEmail}.", toEmail);
                if (throwOnFailure)
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {ToEmail}. Subject: {Subject}.", toEmail, subject);
                if (throwOnFailure)
                {
                    throw;
                }
            }
        }

        private async Task<SmtpSettings> BuildSmtpSettingsAsync(CancellationToken cancellationToken)
        {
            var keys = new[]
            {
                "notifications.email.enabled",
                "notifications.email.smtpHost",
                "notifications.email.smtpPort",
                "notifications.email.smtpUsername",
                "notifications.email.smtpPassword",
                "notifications.email.fromEmail",
                "notifications.email.fromName",
                "notifications.email.enableSsl",
            };
            var settings = await _context.SystemSettings
                .AsNoTracking()
                .Where(setting => keys.Contains(setting.Key))
                .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

            var enabled = GetBool(settings, "notifications.email.enabled", "Email:Enabled", false);
            var host = GetValue(settings, "notifications.email.smtpHost", "Email:SmtpHost");
            var port = GetInt(settings, "notifications.email.smtpPort", "Email:SmtpPort", DefaultSmtpPort);
            var username = GetValue(settings, "notifications.email.smtpUsername", "Email:SmtpUsername");
            var password = GetValue(settings, "notifications.email.smtpPassword", "Email:SmtpPassword");
            var fromEmail = GetValue(settings, "notifications.email.fromEmail", "Email:FromEmail");
            var fromName = string.IsNullOrWhiteSpace(GetValue(settings, "notifications.email.fromName", "Email:FromName"))
                ? DefaultFromName
                : GetValue(settings, "notifications.email.fromName", "Email:FromName")!;
            var enableSsl = GetBool(settings, "notifications.email.enableSsl", "Email:EnableSsl", true);

            var isReady =
                !string.IsNullOrWhiteSpace(host) &&
                !string.IsNullOrWhiteSpace(username) &&
                !string.IsNullOrWhiteSpace(password) &&
                !string.IsNullOrWhiteSpace(fromEmail);

            return new SmtpSettings(
                enabled,
                isReady,
                host ?? string.Empty,
                port,
                username ?? string.Empty,
                password ?? string.Empty,
                fromEmail ?? string.Empty,
                fromName,
                enableSsl);
        }

        private string? GetValue(IReadOnlyDictionary<string, string> settings, string settingKey, string configPath)
        {
            if (settings.TryGetValue(settingKey, out var stored) && !string.IsNullOrWhiteSpace(stored))
            {
                return stored.Trim();
            }

            var value = _configuration[configPath];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private int GetInt(IReadOnlyDictionary<string, string> settings, string settingKey, string configPath, int fallback)
        {
            var raw = GetValue(settings, settingKey, configPath);
            return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        private bool GetBool(IReadOnlyDictionary<string, string> settings, string settingKey, string configPath, bool fallback)
        {
            var raw = GetValue(settings, settingKey, configPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            return raw.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "on" => true,
                "0" or "false" or "no" or "off" => false,
                _ => fallback
            };
        }

        public Task SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            CancellationToken cancellationToken = default,
            EmailSendSettings? settings = null)
        {
            return SendEmailCoreAsync(
                toEmail,
                subject,
                body,
                isHtml,
                cancellationToken: cancellationToken,
                throwOnFailure: true,
                overrideSettings: settings);
        }

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
        {
            return SendEmailCoreAsync(
                adminEmail,
                "Новая регистрация в CRM",
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
            return SendEmailCoreAsync(toEmail, subject, body, configureMessage: message =>
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
                "Регистрация одобрена",
                BuildRegistrationApprovedBody(fullName));
        }

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
        {
            return SendEmailCoreAsync(
                toEmail,
                "Регистрация ожидает подтверждения",
                BuildRegistrationPendingBody(fullName));
        }
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
        {
            return SendEmailCoreAsync(
                toEmail,
                "Регистрация отклонена",
                BuildRegistrationRejectedBody(fullName, reason));
        }

        private readonly record struct SmtpSettings(
            bool Enabled,
            bool IsReady,
            string Host,
            int Port,
            string Username,
            string Password,
            string FromEmail,
            string FromName,
            bool EnableSsl);

        private static SmtpSettings ConvertSettings(EmailSendSettings settings)
        {
            var isReady =
                !string.IsNullOrWhiteSpace(settings.Host) &&
                !string.IsNullOrWhiteSpace(settings.Username) &&
                !string.IsNullOrWhiteSpace(settings.Password) &&
                !string.IsNullOrWhiteSpace(settings.FromEmail);

            return new SmtpSettings(
                settings.Enabled,
                isReady,
                settings.Host,
                settings.Port,
                settings.Username,
                settings.Password,
                settings.FromEmail,
                string.IsNullOrWhiteSpace(settings.FromName) ? DefaultFromName : settings.FromName,
                settings.EnableSsl);
        }
    }
}
