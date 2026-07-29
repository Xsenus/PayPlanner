using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsMessageProtectorTests
{
    [Fact]
    public void Protect_ReplacesMessageExplicitlyMarkedAsSensitive()
    {
        var message = new NotificationMessage(
            "Доступ",
            "Временный пароль: SuperSecret123",
            IsSensitive: true,
            Purpose: NotificationPurposes.PasswordIssue);

        var result = SmsMessageProtector.Protect(message);

        Assert.True(result.WasReplaced);
        Assert.Equal(SmsMessageProtectionReasonCodes.SensitiveMessage, result.ReasonCode);
        Assert.DoesNotContain("SuperSecret123", result.Body, StringComparison.Ordinal);
        Assert.Contains("защищённом канале", result.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Паспорт: 45 01 123456")]
    [InlineData("Код подразделения: 770-001")]
    [InlineData("Authorization: Bearer token-value")]
    [InlineData("client_secret=secret-value")]
    [InlineData("token=raw-provider-token")]
    public void Protect_ReplacesBodyContainingSensitiveMarker(string body)
    {
        var result = SmsMessageProtector.Protect(new NotificationMessage("Уведомление", body));

        Assert.True(result.WasReplaced);
        Assert.Equal(SmsMessageProtectionReasonCodes.SensitiveMarker, result.ReasonCode);
        Assert.DoesNotContain(body, result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_ChecksSubjectForSensitiveMarkers()
    {
        var result = SmsMessageProtector.Protect(
            new NotificationMessage("Пароль пользователя", "Доступ обновлён"));

        Assert.True(result.WasReplaced);
        Assert.Equal(SmsMessageProtectionReasonCodes.SensitiveMarker, result.ReasonCode);
        Assert.DoesNotContain("Пароль", result.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Protect_ReplacesBodyWithTooManyDetailedLines()
    {
        var result = SmsMessageProtector.Protect(
            new NotificationMessage("Дело", "Статус: новый\nСуд: районный\nДокумент: заявление\nКомментарий: проверить"));

        Assert.True(result.WasReplaced);
        Assert.Equal(SmsMessageProtectionReasonCodes.ExcessiveDetails, result.ReasonCode);
    }

    [Fact]
    public void Protect_ReplacesBodyAboveDetailedUnitLimit()
    {
        var result = SmsMessageProtector.Protect(
            new NotificationMessage("Дело", new string('Я', SmsMessageProtector.MaxDetailedBodyUnitCount + 1)));

        Assert.True(result.WasReplaced);
        Assert.Equal(SmsMessageProtectionReasonCodes.ExcessiveDetails, result.ReasonCode);
        Assert.True(
            SmsSegmentCalculator.Calculate(result.Body).UnitCount
            < SmsMessageProtector.MaxDetailedBodyUnitCount);
    }

    [Fact]
    public void Protect_CompactsSafeMessageWithoutChangingItsMeaning()
    {
        var result = SmsMessageProtector.Protect(
            new NotificationMessage("  Срок задачи  ", "CRM:   задача готова.\r\nСрок: завтра."));

        Assert.False(result.WasReplaced);
        Assert.Null(result.ReasonCode);
        Assert.Equal("Срок задачи", result.Subject);
        Assert.Equal("CRM: задача готова. Срок: завтра.", result.Body);
    }

    [Theory]
    [InlineData(NotificationPurposes.TaskReminder)]
    [InlineData(NotificationPurposes.ContractClient)]
    [InlineData(NotificationPurposes.RegistrationUser)]
    [InlineData(NotificationPurposes.PasswordIssue)]
    [InlineData(NotificationPurposes.ManualTest)]
    public void Protect_KeepsSafeReplacementWithinOneUnicodeSegment(string purpose)
    {
        var result = SmsMessageProtector.Protect(
            new NotificationMessage("Закрыто", "Скрыто", IsSensitive: true, Purpose: purpose));

        var calculation = SmsSegmentCalculator.Calculate(result.Body);

        Assert.Equal(SmsTextEncoding.Unicode, calculation.Encoding);
        Assert.Equal(1, calculation.SegmentCount);
    }
}
