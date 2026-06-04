using System.Globalization;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CalendarTimeZoneHelperTests
{
    [Fact]
    public void FormatUserDateTime_UsesProvidedTimeZone()
    {
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var novosibirsk = CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk");
        var value = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            "04.06.2026 16:00",
            CalendarTimeZoneHelper.FormatUserDateTime(value, samara, culture: CultureInfo.GetCultureInfo("ru-RU")));
        Assert.Equal(
            "04.06.2026 19:00",
            CalendarTimeZoneHelper.FormatUserDateTime(value, novosibirsk, culture: CultureInfo.GetCultureInfo("ru-RU")));
    }

    [Fact]
    public void PaymentSystemNote_UsesExplicitTimeZoneForTimestamp()
    {
        var payment = new Payment();
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var value = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

        PaymentDomainService.AppendSystemNote(payment, "Test note", value, "Tester", samara);

        Assert.Contains("04.06.2026 (16:00:00)", payment.SystemNotes, StringComparison.Ordinal);
        Assert.Contains("Tester: Test note", payment.SystemNotes, StringComparison.Ordinal);
    }
}
