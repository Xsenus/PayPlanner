using System.Net;
using System.Net.Mail;

namespace PayPlanner.Api.Services;

/// <summary>
/// C����� �������� �������� ����������� (�����������, ���������/���������� � ����������� ��������������).
/// ���������� SMTP-���������� �� ������������ (<c>Email:*</c>).
/// </summary>
public sealed class EmailService : IEmailService
{
    private const string DefaultFromName = "PayPlanner";
    private const int DefaultSmtpPort = 587;

    private readonly IConfiguration _configuration;
    private readonly bool _isEnabled;
    private readonly ILogger<EmailService> _logger;

    /// <summary>
    /// ������ ��������� <see cref="EmailService"/>.
    /// </summary>
    /// <param name="configuration">��������� ������������ (������������ ������ <c>Email</c>).</param>
    /// <param name="logger">������.</param>
    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _isEnabled = _configuration.GetValue<bool>("Email:Enabled", false);
    }

    #region Public API

    /// <summary>
    /// ���������� �������������� ����������� � ����� �����������, ��������� �������.
    /// </summary>
    /// <param name="adminEmail">E-mail ��������������.</param>
    /// <param name="newUserFullName">��� ��������������������� ������������.</param>
    /// <param name="newUserEmail">E-mail ��������������������� ������������.</param>
    public async Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
    {
        var subject = "����� ����������� ������������ � ��������� ���������";
        var body = EmailTemplates.AdminNewRegistration(newUserFullName, newUserEmail);
        await SendEmailAsync(adminEmail, subject, body);
    }

    /// <summary>
    /// ���������� ������ ������������ � ���, ��� ������� ������ ��������.
    /// </summary>
    /// <param name="toEmail">E-mail ������������.</param>
    /// <param name="fullName">��� ������������.</param>
    public async Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
    {
        var subject = "������� ������ �������� � ����� ���������� � PayPlanner";
        var body = EmailTemplates.RegistrationApproved(fullName);
        await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    /// ���������� ������ ������������ � ���, ��� ����������� �������� � ������� ��������� ��������������.
    /// </summary>
    /// <param name="toEmail">E-mail ������������.</param>
    /// <param name="fullName">��� ������������.</param>
    public async Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
    {
        var subject = "����������� �������� � ������� ���������";
        var body = EmailTemplates.RegistrationPending(fullName);
        await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    /// ���������� ������ ������������ � ���, ��� ����������� �� ��������.
    /// </summary>
    /// <param name="toEmail">E-mail ������������.</param>
    /// <param name="fullName">��� ������������.</param>
    /// <param name="reason">�������������� ������� ����������.</param>
    public async Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
    {
        var subject = "����������� �� ��������";
        var body = EmailTemplates.RegistrationRejected(fullName, reason);
        await SendEmailAsync(toEmail, subject, body);
    }

    #endregion

    #region Core send

    /// <summary>
    /// ������������� �������� ������ ����� SMTP �� ���������� �� ������ <c>Email</c>.
    /// ���� �������� ��������� (<c>Email:Enabled = false</c>), ����������� ������ ����������� ("dry-run").
    /// </summary>
    /// <param name="toEmail">����������.</param>
    /// <param name="subject">���� ������.</param>
    /// <param name="body">����� ������ (������� �����, ��� HTML).</param>
    private async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation(
                "�������� ����� ���������. ������ ��� {ToEmail} � ����� \"{Subject}\" �� ���������� (dry-run).",
                toEmail, subject);
            return;
        }

        if (!TryBuildSmtpSettings(out var settings))
        {
            _logger.LogWarning("������������ ����� �������� � �������� ������ {ToEmail} ��������.", toEmail);
            return;
        }

        try
        {
            // ���. ��������� ������� ������ (������ ������ ������ ���������� SMTP-����������)
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
            _logger.LogInformation("������ ������� ���������� �� {ToEmail}: {Subject}", toEmail, subject);
        }
        catch (FormatException fex)
        {
            _logger.LogError(fex, "������������ ����� ����������: {ToEmail}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "�� ������� ��������� ������ �� {ToEmail}: {Subject}", toEmail, subject);
        }
    }

    /// <summary>
    /// ������ � ���������� ��������� SMTP �� ������������.
    /// </summary>
    /// <param name="settings">�������� ��������� SMTP.</param>
    /// <returns><c>true</c>, ���� ��������� ���������; ����� <c>false</c>.</returns>
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
    /// ����� �������� ��� � ������� �����.
    /// </summary>
    private static class EmailTemplates
    {
        public static string RegistrationPending(string fullName) =>
$@"������������, {fullName}!

������� �� ����������� � PayPlanner.

���� ������� ������ ������� � ������� ��������� ���������������.
��� ������ ������ ����� �������, �� �������� �������������� ������ � ������� ����� � �������.

���� � ��� ���� �������, ���������� � �������������� �������.

� ���������,
������� PayPlanner";

        public static string RegistrationApproved(string fullName) =>
$@"������������, {fullName}!

�������� ������� � ���� ������� ������ PayPlanner ��������.

������ �� ������ ����� � �������, ��������� ���� e-mail � ������.

����� ����������!

� ���������,
������� PayPlanner";

        public static string RegistrationRejected(string fullName, string? reason)
        {
            var reasonLine = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"�������: {reason}{Environment.NewLine}";
            return
$@"������������, {fullName}!

� ���������, ���� ����������� � PayPlanner �� ���� ��������.
{reasonLine}���� �� ��������, ��� ��������� ������, ���������� � �������������� �������.

� ���������,
������� PayPlanner";
        }

        public static string AdminNewRegistration(string userFullName, string userEmail) =>
$@"������������!

��������� ����� ����������� � ������� ������ �������:

���: {userFullName}
E-mail: {userEmail}

����������, ������� � ������ ��������������, ����� �������� ��� ��������� ������.

� ���������,
������� PayPlanner";
    }

    /// <summary>
    /// ������������� ��������� SMTP, ��������� �� ������������.
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
