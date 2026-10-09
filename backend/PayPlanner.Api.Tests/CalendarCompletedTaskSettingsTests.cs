using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CalendarCompletedTaskSettingsTests
{
    [Theory]
    [InlineData("hide", "hide")]
    [InlineData("color", "color")]
    [InlineData(null, "color")]
    [InlineData("", "color")]
    [InlineData("delete", "color")]
    [InlineData("HIDE", "color")]
    public void NormalizeSettings_KeepsOnlyKnownCompletedTaskActions(string? input, string expected)
    {
        var normalized = CalendarSyncProviderRegistry.NormalizeSettings(
            new CalendarSyncSettingsDto { CompletedTaskCalendarAction = input ?? string.Empty },
            externalCalendarSyncEnabledFallback: false);

        Assert.Equal(expected, normalized.CompletedTaskCalendarAction);
    }

    [Fact]
    public void DefaultSettings_KeepCompletedTasksVisibleWithColor()
    {
        var defaults = CalendarSyncProviderRegistry.BuildDefaultSettings();

        Assert.Equal("color", defaults.CompletedTaskCalendarAction);
    }
}
