using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsMessageValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void Validate_RejectsEmptyBody(string? text)
    {
        var result = SmsMessageValidator.Validate(text);

        Assert.False(result.IsValid);
        Assert.Equal(SmsMessageValidationErrorCodes.EmptyBody, result.ErrorCode);
    }

    [Theory]
    [InlineData("Задача {{task.title}}")]
    [InlineData("Задача {{task.title")]
    [InlineData("Задача task.title}}")]
    public void Validate_RejectsUnresolvedTemplateMarkers(string text)
    {
        var result = SmsMessageValidator.Validate(text);

        Assert.False(result.IsValid);
        Assert.Equal(SmsMessageValidationErrorCodes.UnresolvedTemplate, result.ErrorCode);
    }

    [Fact]
    public void Validate_AcceptsMessageAtConfiguredGsm7UnitLimit()
    {
        var result = SmsMessageValidator.Validate(new string('^', 80), maxUnitCount: 160);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorCode);
        Assert.Equal(SmsTextEncoding.Gsm7, result.Calculation.Encoding);
        Assert.Equal(160, result.Calculation.UnitCount);
        Assert.Equal(1, result.Calculation.SegmentCount);
    }

    [Fact]
    public void Validate_RejectsMessageAboveConfiguredGsm7UnitLimit()
    {
        var result = SmsMessageValidator.Validate(new string('^', 81), maxUnitCount: 160);

        Assert.False(result.IsValid);
        Assert.Equal(SmsMessageValidationErrorCodes.TooLong, result.ErrorCode);
        Assert.Equal(162, result.Calculation.UnitCount);
        Assert.Equal(2, result.Calculation.SegmentCount);
    }

    [Fact]
    public void Validate_UsesUnicodeUnitsForConfiguredLimit()
    {
        var result = SmsMessageValidator.Validate(new string('Я', 71), maxUnitCount: 70);

        Assert.False(result.IsValid);
        Assert.Equal(SmsMessageValidationErrorCodes.TooLong, result.ErrorCode);
        Assert.Equal(SmsTextEncoding.Unicode, result.Calculation.Encoding);
        Assert.Equal(71, result.Calculation.UnitCount);
        Assert.Equal(2, result.Calculation.SegmentCount);
    }

    [Fact]
    public void Validate_RejectsNonPositiveLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SmsMessageValidator.Validate("Сообщение", maxUnitCount: 0));
    }
}
