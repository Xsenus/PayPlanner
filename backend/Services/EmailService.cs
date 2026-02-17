using System.IO;
using System.Net;
using System.Net.Mail;

namespace PayPlanner.Api.Services;

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

    public async Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
    {
        var subject = "Новая регистрация пользователя в системе PayPlanner";
        var body = EmailTemplates.AdminNewRegistration(newUserFullName, newUserEmail);
        await SendEmailCoreAsync(adminEmail, subject, body);
    }

    public async Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
    {
        var subject = "Ваша заявка одобрена. Доступ к PayPlanner открыт";
        var body = EmailTemplates.RegistrationApproved(fullName);
        await SendEmailCoreAsync(toEmail, subject, body);
    }

        var subject = "Заявка принята и находится на модерации";
        await SendEmailCoreAsync(toEmail, subject, body);
        var subject = "Регистрация отклонена";
        await SendEmailCoreAsync(toEmail, subject, body);
        await SendEmailCoreAsync(toEmail, subject, body, message =>

    }

    private async Task SendEmailCoreAsync(
        string toEmail,
        string subject,
        string body,
        Action<MailMessage>? configureMessage = null)
                "Email disabled. Dry-run for {ToEmail}, subject: {Subject}.",
                toEmail,
                subject);
            _logger.LogWarning("SMTP settings are invalid. Email to {ToEmail} skipped.", toEmail);
                EnableSsl = settings.EnableSsl,
                IsBodyHtml = false,
            _logger.LogInformation("Email sent to {ToEmail}. Subject: {Subject}", toEmail, subject);
            _logger.LogError(fex, "Invalid recipient email format: {ToEmail}", toEmail);
            _logger.LogError(ex, "Email send failed to {ToEmail}. Subject: {Subject}", toEmail, subject);
        var ok =
            !string.IsNullOrWhiteSpace(host)
            && !string.IsNullOrWhiteSpace(username)
            && !string.IsNullOrWhiteSpace(password)
            && !string.IsNullOrWhiteSpace(fromEmail);
$@"Здравствуйте, {fullName}!
Спасибо за регистрацию в PayPlanner.
Ваша заявка принята и находится на модерации.
После проверки вам придет уведомление на этот email.
С уважением,
Команда PayPlanner";
$@"Здравствуйте, {fullName}!

Ваша заявка одобрена, доступ к PayPlanner открыт.
С уважением,
Команда PayPlanner";
            var reasonLine = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"Причина: {reason}{Environment.NewLine}";
$@"Здравствуйте, {fullName}!
К сожалению, ваша регистрация не одобрена.
{reasonLine}
С уважением,
Команда PayPlanner";

$@"Здравствуйте!
Поступила новая регистрация:
Имя: {userFullName}
С уважением,
PayPlanner";
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
