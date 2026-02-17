using System.IO;
using System.Net;
using System.Net.Mail;

namespace PayPlanner.Api.Services
{
    public sealed class EmailService : IEmailService
        private const string DefaultFromName = "PayPlanner";
        private const int DefaultSmtpPort = 587;

        private readonly IConfiguration _configuration;
        private readonly bool _isEnabled;
        private readonly ILogger<EmailService> _logger;
        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
            _configuration = configuration;
            _logger = logger;
            _isEnabled = _configuration.GetValue<bool>("Email:Enabled", false);
        }
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
        {
            var subject = "Заявка принята и находится на модерации";
            var body = BuildRegistrationPendingBody(fullName);
            return SendEmailCoreAsync(toEmail, subject, body);
        }
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
            var subject = "Ваша заявка одобрена. Доступ к PayPlanner открыт";
            var body = BuildRegistrationApprovedBody(fullName);
            return SendEmailCoreAsync(toEmail, subject, body);
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
            var subject = "Регистрация отклонена";
            var body = BuildRegistrationRejectedBody(fullName, reason);
            return SendEmailCoreAsync(toEmail, subject, body);
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
            var subject = "Новая регистрация пользователя в системе PayPlanner";
            var body = BuildAdminNewRegistrationBody(newUserFullName, newUserEmail);
            return SendEmailCoreAsync(adminEmail, subject, body);
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
                if (attachmentBytes == null || attachmentBytes.Length == 0)
                {
                    return;
                }

                var stream = new MemoryStream(attachmentBytes);
                var attachment = new Attachment(stream, attachmentFileName, attachmentContentType);
                message.Attachments.Add(attachment);
            });
        }
        private async Task SendEmailCoreAsync(
            string toEmail,
            string subject,
            string body,
            Action<MailMessage>? configureMessage = null)
        {
            if (!_isEnabled)
                _logger.LogInformation(
                    "Email disabled. Dry-run for {ToEmail}, subject: {Subject}.",
                    toEmail,
                    subject);
                return;
            }

            if (!TryBuildSmtpSettings(out var settings))
            {
                _logger.LogWarning("SMTP settings are invalid. Email to {ToEmail} skipped.", toEmail);
                return;
            }
            try
            {
                _ = new MailAddress(toEmail);

                using var client = new SmtpClient(settings.Host, settings.Port)
                {
                    Credentials = new NetworkCredential(settings.Username, settings.Password),
                    EnableSsl = settings.EnableSsl
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(settings.FromEmail, settings.FromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false
                };

                message.To.Add(toEmail);
                configureMessage?.Invoke(message);

                await client.SendMailAsync(message);
                _logger.LogInformation("Email sent to {ToEmail}. Subject: {Subject}", toEmail, subject);
            }
            catch (FormatException fex)
            {
                _logger.LogError(fex, "Invalid recipient email format: {ToEmail}", toEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email send failed to {ToEmail}. Subject: {Subject}", toEmail, subject);
            }

        private bool TryBuildSmtpSettings(out SmtpSettings settings)
            var host = _configuration["Email:SmtpHost"]?.Trim();
            var port = _configuration.GetValue<int?>("Email:SmtpPort") ?? DefaultSmtpPort;
            var username = _configuration["Email:SmtpUsername"]?.Trim();
            var password = _configuration["Email:SmtpPassword"];
            var fromEmail = _configuration["Email:FromEmail"]?.Trim();
            var fromName = string.IsNullOrWhiteSpace(_configuration["Email:FromName"])
                ? DefaultFromName
                : _configuration["Email:FromName"]!.Trim();
            var enableSsl = _configuration.GetValue<bool?>("Email:EnableSsl") ?? true;

            var ok =
                !string.IsNullOrWhiteSpace(host) &&
                !string.IsNullOrWhiteSpace(username) &&
                !string.IsNullOrWhiteSpace(password) &&
                !string.IsNullOrWhiteSpace(fromEmail);

            settings = ok
                ? new SmtpSettings(host!, port, username!, password!, fromEmail!, fromName, enableSsl)
                : new SmtpSettings(string.Empty, DefaultSmtpPort, string.Empty, string.Empty, string.Empty, DefaultFromName, true);

            return ok;
        private static string BuildRegistrationPendingBody(string fullName) =>
        private static string BuildRegistrationApprovedBody(string fullName) =>
        private static string BuildRegistrationRejectedBody(string fullName, string? reason)
        private static string BuildAdminNewRegistrationBody(string userFullName, string userEmail) =>
        private sealed class SmtpSettings
        {
            public SmtpSettings(string host, int port, string username, string password, string fromEmail, string fromName, bool enableSsl)
            {
                Host = host;
                Port = port;
                Username = username;
                Password = password;
                FromEmail = fromEmail;
                FromName = fromName;
                EnableSsl = enableSsl;
            }

            public string Host { get; }
            public int Port { get; }
            public string Username { get; }
            public string Password { get; }
            public string FromEmail { get; }
            public string FromName { get; }
            public bool EnableSsl { get; }
        }
    }
Åñëè ó âàñ åñòü âîïðîñû, îáðàòèòåñü ê àäìèíèñòðàòîðó ñèñòåìû.

Ñ óâàæåíèåì,
Êîìàíäà PayPlanner";

        public static string RegistrationApproved(string fullName) =>
$@"Çäðàâñòâóéòå, {fullName}!

Îòëè÷íûå íîâîñòè — âàøà ó÷¸òíàÿ çàïèñü PayPlanner îäîáðåíà.

Òåïåðü âû ìîæåòå âîéòè â ñèñòåìó, èñïîëüçóÿ ñâîé e-mail è ïàðîëü.

Äîáðî ïîæàëîâàòü!

Ñ óâàæåíèåì,
Êîìàíäà PayPlanner";

        public static string RegistrationRejected(string fullName, string? reason)
        {
            var reasonLine = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"Ïðè÷èíà: {reason}{Environment.NewLine}";
            return
$@"Çäðàâñòâóéòå, {fullName}!

Ê ñîæàëåíèþ, âàøà ðåãèñòðàöèÿ â PayPlanner íå áûëà îäîáðåíà.
{reasonLine}Åñëè âû ñ÷èòàåòå, ÷òî ïðîèçîøëà îøèáêà, îáðàòèòåñü ê àäìèíèñòðàòîðó ñèñòåìû.

Ñ óâàæåíèåì,
Êîìàíäà PayPlanner";
        }

        public static string AdminNewRegistration(string userFullName, string userEmail) =>
$@"Çäðàâñòâóéòå!

Ïîñòóïèëà íîâàÿ ðåãèñòðàöèÿ è îæèäàåò âàøåãî ðåøåíèÿ:

Èìÿ: {userFullName}
E-mail: {userEmail}

Ïîæàëóéñòà, âîéäèòå â ïàíåëü àäìèíèñòðàòîðà, ÷òîáû îäîáðèòü èëè îòêëîíèòü çàÿâêó.

Ñ óâàæåíèåì,
Ñèñòåìà PayPlanner";
    }

    /// <summary>
    /// Èììóòàáåëüíûå íàñòðîéêè SMTP, ñîáðàííûå èç êîíôèãóðàöèè.
    /// </summary>
    private readonly record struct SmtpSettings(
        string Host,
        int Port,
        string Username,
        string Password,
        string FromEmail,
        string FromName,
        bool EnableSsl);

    #endregion
}
