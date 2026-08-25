using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RussianPhoneNumberCanonicalizerTests
{
    [Theory]
    [InlineData("+7 (927) 267-87-53", "79272678753")]
    [InlineData("8 927 267 87 53", "79272678753")]
    [InlineData("9272678753", "79272678753")]
    [InlineData("79272678753", "79272678753")]
    public void NormalizeDigits_ReturnsCanonicalRussianNumber(string source, string expected)
        => Assert.Equal(expected, RussianPhoneNumberCanonicalizer.NormalizeDigits(source));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Телефон не указан")]
    public void NormalizeDigits_ReturnsNullWhenNumberHasNoDigits(string? source)
        => Assert.Null(RussianPhoneNumberCanonicalizer.NormalizeDigits(source));

    [Fact]
    public void NormalizeDigits_PreservesUnrecognizedInternationalDigits()
        => Assert.Equal("375291234567", RussianPhoneNumberCanonicalizer.NormalizeDigits("+375 29 123-45-67"));
}
