using System.Text;

namespace PayPlanner.Api.Services.Notifications;

public static class SmsMessageProtectionReasonCodes
{
    public const string SensitiveMessage = "sms-sensitive-message";
    public const string SensitiveMarker = "sms-sensitive-marker";
    public const string ExcessiveDetails = "sms-excessive-details";
}

public sealed record SmsMessageProtectionResult(
    string Subject,
    string Body,
    bool WasReplaced,
    string? ReasonCode);

public static class SmsMessageProtector
{
    public const int MaxDetailedBodyUnitCount = 240;
    private const int MaxDetailedBodyLineCount = 3;

    private static readonly string[] SensitiveMarkers =
    [
        "паспорт",
        "серия и номер",
        "код подразделения",
        "кем выдан",
        "дата рождения",
        "место рождения",
        "снилс",
        "password",
        "пароль",
        "api key",
        "api-key",
        "api_key",
        "apikey",
        "access token",
        "access_token",
        "refresh token",
        "refresh_token",
        "token:",
        "token=",
        "токен:",
        "токен=",
        "bearer ",
        "client secret",
        "client_secret",
        "private key",
        "-----begin ",
        "authorization:",
        "secret:",
        "secret=",
        "секрет:",
        "секрет=",
    ];

    public static SmsMessageProtectionResult Protect(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.IsSensitive)
        {
            return CreateProtected(message.Purpose, SmsMessageProtectionReasonCodes.SensitiveMessage);
        }

        if (ContainsSensitiveMarker(message.Subject) || ContainsSensitiveMarker(message.Body))
        {
            return CreateProtected(message.Purpose, SmsMessageProtectionReasonCodes.SensitiveMarker);
        }

        var compactBody = CompactWhitespace(message.Body);
        var calculation = SmsSegmentCalculator.Calculate(compactBody);
        if (CountNonEmptyLines(message.Body) > MaxDetailedBodyLineCount
            || calculation.UnitCount > MaxDetailedBodyUnitCount)
        {
            return CreateProtected(message.Purpose, SmsMessageProtectionReasonCodes.ExcessiveDetails);
        }

        return new SmsMessageProtectionResult(
            CompactWhitespace(message.Subject),
            compactBody,
            false,
            null);
    }

    private static bool ContainsSensitiveMarker(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && SensitiveMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static int CountNonEmptyLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        var hasContent = false;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                if (hasContent)
                {
                    count++;
                    hasContent = false;
                }

                continue;
            }

            if (!char.IsWhiteSpace(character))
            {
                hasContent = true;
            }
        }

        return hasContent ? count + 1 : count;
    }

    private static string CompactWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static SmsMessageProtectionResult CreateProtected(string purpose, string reasonCode)
    {
        var body = purpose switch
        {
            NotificationPurposes.TaskReminder
                => "CRM: новое уведомление по задаче. Подробности — в CRM.",
            NotificationPurposes.ContractClient
                => "CRM: новое уведомление по договору. Подробности — в CRM.",
            NotificationPurposes.RegistrationUser or NotificationPurposes.RegistrationAdmin
                => "CRM: статус доступа изменён. Подробности — в CRM.",
            NotificationPurposes.PasswordIssue
                => "CRM: данные доступа обновлены. Получите их в защищённом канале.",
            _ => "CRM: новое уведомление. Подробности — в CRM.",
        };

        return new SmsMessageProtectionResult(
            "CRM: защищённое уведомление",
            body,
            true,
            reasonCode);
    }
}
