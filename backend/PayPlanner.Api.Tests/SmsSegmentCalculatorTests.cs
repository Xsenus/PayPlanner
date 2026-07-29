using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsSegmentCalculatorTests
{
    [Theory]
    [InlineData(160, 1, 160)]
    [InlineData(161, 2, 8)]
    [InlineData(306, 2, 153)]
    [InlineData(307, 3, 1)]
    public void Calculate_CountsGsm7Segments(int length, int expectedSegments, int expectedUnitsInLastSegment)
    {
        var result = SmsSegmentCalculator.Calculate(new string('A', length));

        Assert.Equal(SmsTextEncoding.Gsm7, result.Encoding);
        Assert.Equal(length, result.UnitCount);
        Assert.Equal(expectedSegments, result.SegmentCount);
        Assert.Equal(expectedUnitsInLastSegment, result.UnitsInLastSegment);
        Assert.Equal(160, result.SingleSegmentLimit);
        Assert.Equal(153, result.ConcatenatedSegmentLimit);
        Assert.Equal(expectedSegments > 1, result.IsMultipart);
    }

    [Theory]
    [InlineData(80, 1, 160)]
    [InlineData(81, 2, 162)]
    public void Calculate_CountsGsm7ExtensionCharactersAsTwoUnits(
        int characterCount,
        int expectedSegments,
        int expectedUnits)
    {
        var result = SmsSegmentCalculator.Calculate(new string('^', characterCount));

        Assert.Equal(SmsTextEncoding.Gsm7, result.Encoding);
        Assert.Equal(expectedUnits, result.UnitCount);
        Assert.Equal(expectedSegments, result.SegmentCount);
    }

    [Theory]
    [InlineData(70, 1, 70)]
    [InlineData(71, 2, 4)]
    [InlineData(134, 2, 67)]
    [InlineData(135, 3, 1)]
    public void Calculate_CountsUnicodeSegments(int length, int expectedSegments, int expectedUnitsInLastSegment)
    {
        var result = SmsSegmentCalculator.Calculate(new string('Я', length));

        Assert.Equal(SmsTextEncoding.Unicode, result.Encoding);
        Assert.Equal(length, result.UnitCount);
        Assert.Equal(expectedSegments, result.SegmentCount);
        Assert.Equal(expectedUnitsInLastSegment, result.UnitsInLastSegment);
        Assert.Equal(70, result.SingleSegmentLimit);
        Assert.Equal(67, result.ConcatenatedSegmentLimit);
    }

    [Theory]
    [InlineData(35, 1, 70)]
    [InlineData(36, 2, 72)]
    public void Calculate_CountsEmojiAsUtf16SurrogatePairs(
        int emojiCount,
        int expectedSegments,
        int expectedUnits)
    {
        var result = SmsSegmentCalculator.Calculate(string.Concat(Enumerable.Repeat("😀", emojiCount)));

        Assert.Equal(SmsTextEncoding.Unicode, result.Encoding);
        Assert.Equal(expectedUnits, result.UnitCount);
        Assert.Equal(expectedSegments, result.SegmentCount);
    }

    [Fact]
    public void Calculate_UsesUnicodeForMixedText()
    {
        var result = SmsSegmentCalculator.Calculate("Status: готово");

        Assert.Equal(SmsTextEncoding.Unicode, result.Encoding);
        Assert.Equal(14, result.UnitCount);
        Assert.Equal(1, result.SegmentCount);
    }

    [Fact]
    public void Calculate_RecognizesGsm7LineBreaksAndExtensionCharacters()
    {
        var result = SmsSegmentCalculator.Calculate("Line 1\r\nLine 2: €10");

        Assert.Equal(SmsTextEncoding.Gsm7, result.Encoding);
        Assert.Equal(20, result.UnitCount);
        Assert.Equal(1, result.SegmentCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Calculate_ReturnsZeroSegmentsForEmptyText(string? text)
    {
        var result = SmsSegmentCalculator.Calculate(text);

        Assert.Equal(SmsTextEncoding.Gsm7, result.Encoding);
        Assert.Equal(0, result.UnitCount);
        Assert.Equal(0, result.SegmentCount);
        Assert.Equal(0, result.UnitsInLastSegment);
        Assert.False(result.IsMultipart);
    }
}
