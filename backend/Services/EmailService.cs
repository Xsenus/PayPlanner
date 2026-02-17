using System.Net;
using System.Net.Mail;

namespace PayPlanner.Api.Services;

/// <summary>
/// Cåðâèñ îòïðàâêè ïî÷òîâûõ óâåäîìëåíèé (ðåãèñòðàöèÿ, îäîáðåíèå/îòêëîíåíèå è óâåäîìëåíèå àäìèíèñòðàòîðà).
/// Èñïîëüçóåò SMTP-ïðîâàéäåðà èç êîíôèãóðàöèè (<c>Email:*</c>).
/// </summary>
public sealed class EmailService : IEmailService
{
    private const string DefaultFromName = "PayPlanner";
    private const int DefaultSmtpPort = 587;

    private readonly IConfiguration _configuration;
    private readonly bool _isEnabled;
    private readonly ILogger<EmailService> _logger;

    /// <summary>
    /// Ñîçäà¸ò ýêçåìïëÿð <see cref="EmailService"/>.
    /// </summary>
    /// <param name="configuration">Ïðîâàéäåð êîíôèãóðàöèè (èñïîëüçóåòñÿ ñåêöèÿ <c>Email</c>).</param>
    /// <param name="logger">Ëîããåð.</param>
    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _isEnabled = _configuration.GetValue<bool>("Email:Enabled", false);
    }

    #region Public API

    /// <summary>
    /// Îòïðàâëÿåò àäìèíèñòðàòîðó óâåäîìëåíèå î íîâîé ðåãèñòðàöèè, òðåáóþùåé ðåøåíèÿ.
    /// </summary>
    /// <param name="adminEmail">E-mail àäìèíèñòðàòîðà.</param>
    /// <param name="newUserFullName">ÔÈÎ çàðåãèñòðèðîâàâøåãîñÿ ïîëüçîâàòåëÿ.</param>
    /// <param name="newUserEmail">E-mail çàðåãèñòðèðîâàâøåãîñÿ ïîëüçîâàòåëÿ.</param>
    public async Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
    {
        var subject = "Íîâàÿ ðåãèñòðàöèÿ ïîëüçîâàòåëÿ — òðåáóåòñÿ îäîáðåíèå";
        var body = EmailTemplates.AdminNewRegistration(newUserFullName, newUserEmail);
        await SendEmailAsync(adminEmail, subject, body);
    }

    /// <summary>
    /// Îòïðàâëÿåò ïèñüìî ïîëüçîâàòåëþ î òîì, ÷òî ó÷¸òíàÿ çàïèñü îäîáðåíà.
    /// </summary>
    /// <param name="toEmail">E-mail ïîëüçîâàòåëÿ.</param>
    /// <param name="fullName">ÔÈÎ ïîëüçîâàòåëÿ.</param>
    public async Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
    {
        var subject = "Ó÷¸òíàÿ çàïèñü îäîáðåíà — äîáðî ïîæàëîâàòü â PayPlanner";
        var body = EmailTemplates.RegistrationApproved(fullName);
        await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    public async Task SendEmailWithAttachmentAsync(
        string toEmail,
        string subject,
        string body,
        string attachmentFileName,
        byte[] attachmentBytes,
        string attachmentContentType)
    {
        await SendEmailAsync(toEmail, subject, body, message =>
        {
            if (attachmentBytes.Length == 0) return;
            var stream = new MemoryStream(attachmentBytes);
            var attachment = new Attachment(stream, attachmentFileName, attachmentContentType);
            message.Attachments.Add(attachment);
        });
    }

    private async Task SendEmailAsync(string toEmail, string subject, string body, Action<MailMessage>? configureMessage = null)
            configureMessage?.Invoke(message);
    /// </summary>
    /// <param name="toEmail">E-mail ïîëüçîâàòåëÿ.</param>
    /// <param name="fullName">ÔÈÎ ïîëüçîâàòåëÿ.</param>
    public async Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
    {
        var subject = "Ðåãèñòðàöèÿ ïîëó÷åíà — îæèäàåò îäîáðåíèÿ";
        var body = EmailTemplates.RegistrationPending(fullName);
        await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    /// Îòïðàâëÿåò ïèñüìî ïîëüçîâàòåëþ î òîì, ÷òî ðåãèñòðàöèÿ íå îäîáðåíà.
    /// </summary>
    /// <param name="toEmail">E-mail ïîëüçîâàòåëÿ.</param>
    /// <param name="fullName">ÔÈÎ ïîëüçîâàòåëÿ.</param>
    /// <param name="reason">Íåîáÿçàòåëüíàÿ ïðè÷èíà îòêëîíåíèÿ.</param>
    public async Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
    {
        var subject = "Ðåãèñòðàöèÿ íå îäîáðåíà";
        var body = EmailTemplates.RegistrationRejected(fullName, reason);
        await SendEmailAsync(toEmail, subject, body);
    }

    #endregion

    #region Core send

    /// <summary>
    /// Óíèâåðñàëüíàÿ îòïðàâêà ïèñüìà ÷åðåç SMTP ïî íàñòðîéêàì èç ñåêöèè <c>Email</c>.
    /// Åñëè îòïðàâêà îòêëþ÷åíà (<c>Email:Enabled = false</c>), âûïîëíÿåòñÿ òîëüêî ëîãèðîâàíèå ("dry-run").
    /// </summary>
    /// <param name="toEmail">Ïîëó÷àòåëü.</param>
    /// <param name="subject">Òåìà ïèñüìà.</param>
    /// <param name="body">Òåêñò ïèñüìà (îáû÷íûé òåêñò, áåç HTML).</param>
    private async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation(
                "Îòïðàâêà ïèñåì îòêëþ÷åíà. Ïèñüìî äëÿ {ToEmail} ñ òåìîé \"{Subject}\" íå îòïðàâëåíî (dry-run).",
                toEmail, subject);
            return;
        }

        if (!TryBuildSmtpSettings(out var settings))
        {
            _logger.LogWarning("Êîíôèãóðàöèÿ ïî÷òû íåïîëíàÿ — îòïðàâêà ïèñüìà {ToEmail} îòìåíåíà.", toEmail);
            return;
        }

        try
        {
            // Äîï. âàëèäàöèÿ ôîðìàòà àäðåñà (ðàííèé ôèäáåê âìåñòî èñêëþ÷åíèÿ SMTP-ïðîâàéäåðà)
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

            await client.SendMailAsync(message);
            _logger.LogInformation("Ïèñüìî óñïåøíî îòïðàâëåíî íà {ToEmail}: {Subject}", toEmail, subject);
        }
        catch (FormatException fex)
        {
            _logger.LogError(fex, "Íåêîððåêòíûé àäðåñ ïîëó÷àòåëÿ: {ToEmail}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Íå óäàëîñü îòïðàâèòü ïèñüìî íà {ToEmail}: {Subject}", toEmail, subject);
        }
    }

    /// <summary>
    /// ×èòàåò è âàëèäèðóåò íàñòðîéêè SMTP èç êîíôèãóðàöèè.
    /// </summary>
    /// <param name="settings">Âûõîäíûå íàñòðîéêè SMTP.</param>
    /// <returns><c>true</c>, åñëè íàñòðîéêè êîððåêòíû; èíà÷å <c>false</c>.</returns>
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

        var ok = !(string.IsNullOrWhiteSpace(host) ||
                   string.IsNullOrWhiteSpace(username) ||
                   string.IsNullOrWhiteSpace(password) ||
                   string.IsNullOrWhiteSpace(fromEmail));

        settings = ok
            ? new SmtpSettings(host!, port, username!, password!, fromEmail!, fromName, enableSsl)
            : default;

        return ok;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Íàáîð øàáëîíîâ òåì è òåêñòîâ ïèñåì.
    /// </summary>
    private static class EmailTemplates
    {
        public static string RegistrationPending(string fullName) =>
$@"Çäðàâñòâóéòå, {fullName}!

Ñïàñèáî çà ðåãèñòðàöèþ â PayPlanner.

Âàøà ó÷¸òíàÿ çàïèñü ñîçäàíà è îæèäàåò îäîáðåíèÿ àäìèíèñòðàòîðîì.
Êàê òîëüêî äîñòóï áóäåò îäîáðåí, âû ïîëó÷èòå äîïîëíèòåëüíîå ïèñüìî è ñìîæåòå âîéòè â ñèñòåìó.

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
