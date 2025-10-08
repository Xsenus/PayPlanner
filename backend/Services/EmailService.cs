using System.Net;
using System.Net.Mail;

namespace PayPlanner.Api.Services;

/// <summary>
/// Cервис отправки почтовых уведомлений (регистрация, одобрение/отклонение и уведомление администратора).
/// Использует SMTP-провайдера из конфигурации (<c>Email:*</c>).
/// </summary>
public sealed class EmailService : IEmailService
{
    private const string DefaultFromName = "PayPlanner";
    private const int DefaultSmtpPort = 587;

    private readonly IConfiguration _configuration;
    private readonly bool _isEnabled;
    private readonly ILogger<EmailService> _logger;

    /// <summary>
    /// Создаёт экземпляр <see cref="EmailService"/>.
    /// </summary>
    /// <param name="configuration">Провайдер конфигурации (используется секция <c>Email</c>).</param>
    /// <param name="logger">Логгер.</param>
    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _isEnabled = _configuration.GetValue<bool>("Email:Enabled", false);
    }

    #region Public API

    /// <summary>
    /// Отправляет администратору уведомление о новой регистрации, требующей решения.
    /// </summary>
    /// <param name="adminEmail">E-mail администратора.</param>
    /// <param name="newUserFullName">ФИО зарегистрировавшегося пользователя.</param>
    /// <param name="newUserEmail">E-mail зарегистрировавшегося пользователя.</param>
    public async Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
    {
        var subject = "Новая регистрация пользователя — требуется одобрение";
        var body = EmailTemplates.AdminNewRegistration(newUserFullName, newUserEmail);
        await SendEmailAsync(adminEmail, subject, body);
    }

    /// <summary>
    /// Отправляет письмо пользователю о том, что учётная запись одобрена.
    /// </summary>
    /// <param name="toEmail">E-mail пользователя.</param>
    /// <param name="fullName">ФИО пользователя.</param>
    public async Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
    {
        var subject = "Учётная запись одобрена — добро пожаловать в PayPlanner";
        var body = EmailTemplates.RegistrationApproved(fullName);
        await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    /// Отправляет письмо пользователю о том, что регистрация получена и ожидает одобрения администратора.
    /// </summary>
    /// <param name="toEmail">E-mail пользователя.</param>
    /// <param name="fullName">ФИО пользователя.</param>
    public async Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
    {
        var subject = "Регистрация получена — ожидает одобрения";
        var body = EmailTemplates.RegistrationPending(fullName);
        await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    /// Отправляет письмо пользователю о том, что регистрация не одобрена.
    /// </summary>
    /// <param name="toEmail">E-mail пользователя.</param>
    /// <param name="fullName">ФИО пользователя.</param>
    /// <param name="reason">Необязательная причина отклонения.</param>
    public async Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
    {
        var subject = "Регистрация не одобрена";
        var body = EmailTemplates.RegistrationRejected(fullName, reason);
        await SendEmailAsync(toEmail, subject, body);
    }

    #endregion

    #region Core send

    /// <summary>
    /// Универсальная отправка письма через SMTP по настройкам из секции <c>Email</c>.
    /// Если отправка отключена (<c>Email:Enabled = false</c>), выполняется только логирование ("dry-run").
    /// </summary>
    /// <param name="toEmail">Получатель.</param>
    /// <param name="subject">Тема письма.</param>
    /// <param name="body">Текст письма (обычный текст, без HTML).</param>
    private async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation(
                "Отправка писем отключена. Письмо для {ToEmail} с темой \"{Subject}\" не отправлено (dry-run).",
                toEmail, subject);
            return;
        }

        if (!TryBuildSmtpSettings(out var settings))
        {
            _logger.LogWarning("Конфигурация почты неполная — отправка письма {ToEmail} отменена.", toEmail);
            return;
        }

        try
        {
            // Доп. валидация формата адреса (ранний фидбек вместо исключения SMTP-провайдера)
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
            _logger.LogInformation("Письмо успешно отправлено на {ToEmail}: {Subject}", toEmail, subject);
        }
        catch (FormatException fex)
        {
            _logger.LogError(fex, "Некорректный адрес получателя: {ToEmail}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось отправить письмо на {ToEmail}: {Subject}", toEmail, subject);
        }
    }

    /// <summary>
    /// Читает и валидирует настройки SMTP из конфигурации.
    /// </summary>
    /// <param name="settings">Выходные настройки SMTP.</param>
    /// <returns><c>true</c>, если настройки корректны; иначе <c>false</c>.</returns>
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
    /// Набор шаблонов тем и текстов писем.
    /// </summary>
    private static class EmailTemplates
    {
        public static string RegistrationPending(string fullName) =>
$@"Здравствуйте, {fullName}!

Спасибо за регистрацию в PayPlanner.

Ваша учётная запись создана и ожидает одобрения администратором.
Как только доступ будет одобрен, вы получите дополнительное письмо и сможете войти в систему.

Если у вас есть вопросы, обратитесь к администратору системы.

С уважением,
Команда PayPlanner";

        public static string RegistrationApproved(string fullName) =>
$@"Здравствуйте, {fullName}!

Отличные новости — ваша учётная запись PayPlanner одобрена.

Теперь вы можете войти в систему, используя свой e-mail и пароль.

Добро пожаловать!

С уважением,
Команда PayPlanner";

        public static string RegistrationRejected(string fullName, string? reason)
        {
            var reasonLine = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"Причина: {reason}{Environment.NewLine}";
            return
$@"Здравствуйте, {fullName}!

К сожалению, ваша регистрация в PayPlanner не была одобрена.
{reasonLine}Если вы считаете, что произошла ошибка, обратитесь к администратору системы.

С уважением,
Команда PayPlanner";
        }

        public static string AdminNewRegistration(string userFullName, string userEmail) =>
$@"Здравствуйте!

Поступила новая регистрация и ожидает вашего решения:

Имя: {userFullName}
E-mail: {userEmail}

Пожалуйста, войдите в панель администратора, чтобы одобрить или отклонить заявку.

С уважением,
Система PayPlanner";
    }

    /// <summary>
    /// Иммутабельные настройки SMTP, собранные из конфигурации.
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
