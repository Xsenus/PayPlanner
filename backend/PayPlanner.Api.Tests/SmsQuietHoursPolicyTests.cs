using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsQuietHoursPolicyTests
{
    [Fact]
    public void Evaluate_AllowsWhenQuietHoursAreDisabled()
    {
        var settings = CreateSettings(enabled: false);

        var decision = SmsQuietHoursPolicy.Evaluate(
            settings,
            new DateTimeOffset(2026, 7, 30, 20, 0, 0, TimeSpan.Zero),
            isApprovedCriticalEvent: false);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.IsQuietHours);
    }

    [Theory]
    [InlineData(18, 59, true)]
    [InlineData(19, 0, false)]
    [InlineData(4, 59, false)]
    [InlineData(5, 0, true)]
    public void Evaluate_UsesConfiguredTimeZoneAndOvernightWindow(
        int utcHour,
        int utcMinute,
        bool expectedAllowed)
    {
        var settings = CreateSettings();

        var decision = SmsQuietHoursPolicy.Evaluate(
            settings,
            new DateTimeOffset(2026, 7, 30, utcHour, utcMinute, 0, TimeSpan.Zero),
            isApprovedCriticalEvent: false);

        Assert.Equal(expectedAllowed, decision.IsAllowed);
        Assert.Equal(!expectedAllowed, decision.IsQuietHours);
    }

    [Fact]
    public void Evaluate_AllowsOnlyExplicitlyApprovedCriticalEventDuringQuietHours()
    {
        var settings = CreateSettings();
        var quietInstant = new DateTimeOffset(2026, 7, 30, 21, 0, 0, TimeSpan.Zero);

        var ordinary = SmsQuietHoursPolicy.Evaluate(settings, quietInstant, isApprovedCriticalEvent: false);
        var approvedCritical = SmsQuietHoursPolicy.Evaluate(settings, quietInstant, isApprovedCriticalEvent: true);

        Assert.False(ordinary.IsAllowed);
        Assert.Equal(SmsQuietHoursPolicy.QuietHoursReasonCode, ordinary.ReasonCode);
        Assert.True(approvedCritical.IsAllowed);
        Assert.True(approvedCritical.IsQuietHours);
    }

    [Fact]
    public void Evaluate_TreatsEqualBoundariesAsFullDayQuietHours()
    {
        var settings = CreateSettings();
        settings.QuietHoursStart = "12:00";
        settings.QuietHoursEnd = "12:00";

        var decision = SmsQuietHoursPolicy.Evaluate(
            settings,
            new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero),
            isApprovedCriticalEvent: false);

        Assert.False(decision.IsAllowed);
        Assert.True(decision.IsQuietHours);
    }

    private static SmsNotificationSettingsDto CreateSettings(bool enabled = true)
        => new()
        {
            Enabled = true,
            QuietHoursEnabled = enabled,
            QuietHoursStart = "22:00",
            QuietHoursEnd = "08:00",
            QuietHoursTimeZone = "Europe/Moscow",
        };
}
