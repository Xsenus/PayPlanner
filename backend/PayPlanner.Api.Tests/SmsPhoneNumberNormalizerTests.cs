using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsPhoneNumberNormalizerTests
{
    [Theory]
    [InlineData("+7 (999) 111-22-33", "+79991112233")]
    [InlineData("8 999 111 22 33", "+79991112233")]
    [InlineData("9991112233", "+79991112233")]
    [InlineData("79991112233", "+79991112233")]
    [InlineData("00 7 999 111 22 33", "+79991112233")]
    [InlineData("+7/999/111.22.33", "+79991112233")]
    public void Normalize_ConvertsRussianNumbersToE164(string source, string expected)
    {
        var result = SmsPhoneNumberNormalizer.Normalize(source);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.E164);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Normalize_AllowsExplicitlyApprovedForeignCountry()
    {
        var policy = new SmsPhoneNumberPolicy(["7", "+375"]);

        var result = SmsPhoneNumberNormalizer.Normalize("+375 (29) 123-45-67", policy);

        Assert.True(result.IsValid);
        Assert.Equal("+375291234567", result.E164);
    }

    [Fact]
    public void Normalize_RejectsForeignCountryByDefault()
    {
        var result = SmsPhoneNumberNormalizer.Normalize("+375291234567");

        Assert.False(result.IsValid);
        Assert.Null(result.E164);
        Assert.Equal(SmsPhoneNumberErrorCodes.CountryNotAllowed, result.ErrorCode);
    }

    [Theory]
    [InlineData(null, SmsPhoneNumberErrorCodes.Empty)]
    [InlineData(" ", SmsPhoneNumberErrorCodes.Empty)]
    [InlineData("+7 999 CALL NOW", SmsPhoneNumberErrorCodes.InvalidFormat)]
    [InlineData("7+9991112233", SmsPhoneNumberErrorCodes.InvalidFormat)]
    [InlineData("+7999+1112233", SmsPhoneNumberErrorCodes.InvalidFormat)]
    [InlineData("+7999111", SmsPhoneNumberErrorCodes.InvalidLength)]
    [InlineData("+7999111223344556", SmsPhoneNumberErrorCodes.InvalidLength)]
    [InlineData("+712345678901", SmsPhoneNumberErrorCodes.InvalidLength)]
    [InlineData("375291234567", SmsPhoneNumberErrorCodes.InvalidFormat)]
    public void Normalize_RejectsUnsafeOrAmbiguousInput(string? source, string expectedError)
    {
        var result = SmsPhoneNumberNormalizer.Normalize(source);

        Assert.False(result.IsValid);
        Assert.Null(result.E164);
        Assert.Equal(expectedError, result.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1234")]
    [InlineData("+1234")]
    [InlineData("abc")]
    public void Policy_RejectsInvalidCountryCallingCode(string callingCode)
    {
        Assert.Throws<ArgumentException>(() => new SmsPhoneNumberPolicy([callingCode]));
    }

    [Fact]
    public void Policy_RejectsEmptyAllowedCountryList()
    {
        Assert.Throws<ArgumentException>(() => new SmsPhoneNumberPolicy([]));
    }
}
