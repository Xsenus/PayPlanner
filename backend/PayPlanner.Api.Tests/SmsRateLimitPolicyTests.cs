using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsRateLimitPolicyTests
{
    [Fact]
    public void FromSettings_UsesConfiguredValues()
    {
        var policy = SmsRateLimitPolicy.FromSettings(new SmsNotificationSettingsDto
        {
            DailyLimit = 120,
            RecipientDailyLimit = 7,
            BurstLimitPerMinute = 25,
        });

        Assert.Equal(120, policy.DailyLimit);
        Assert.Equal(7, policy.RecipientDailyLimit);
        Assert.Equal(25, policy.BurstLimitPerMinute);
    }

    [Fact]
    public void FromSettings_UsesSafeDefaultsForMissingOrInvalidValues()
    {
        var policy = SmsRateLimitPolicy.FromSettings(new SmsNotificationSettingsDto());

        Assert.Equal(100, policy.DailyLimit);
        Assert.Equal(5, policy.RecipientDailyLimit);
        Assert.Equal(20, policy.BurstLimitPerMinute);
    }

    [Theory]
    [InlineData(99, 4, 19)]
    [InlineData(0, 0, 0)]
    public void Evaluate_AllowsCountsBelowLimits(int daily, int recipientDaily, int burst)
    {
        var decision = SmsRateLimitEvaluator.Evaluate(
            new SmsRateLimitPolicy(100, 5, 20),
            new SmsRateLimitSnapshot(daily, recipientDaily, burst));

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.ReasonCode);
    }

    [Theory]
    [InlineData(100, 0, 0, SmsRateLimitReasonCodes.DailyLimitReached)]
    [InlineData(0, 5, 0, SmsRateLimitReasonCodes.RecipientDailyLimitReached)]
    [InlineData(0, 0, 20, SmsRateLimitReasonCodes.BurstLimitReached)]
    public void Evaluate_RejectsReachedLimit(
        int daily,
        int recipientDaily,
        int burst,
        string expectedReason)
    {
        var decision = SmsRateLimitEvaluator.Evaluate(
            new SmsRateLimitPolicy(100, 5, 20),
            new SmsRateLimitSnapshot(daily, recipientDaily, burst));

        Assert.False(decision.IsAllowed);
        Assert.Equal(expectedReason, decision.ReasonCode);
    }

    [Fact]
    public void Evaluate_AppliesGlobalDailyLimitBeforeNarrowerLimits()
    {
        var decision = SmsRateLimitEvaluator.Evaluate(
            new SmsRateLimitPolicy(100, 5, 20),
            new SmsRateLimitSnapshot(100, 5, 20));

        Assert.Equal(SmsRateLimitReasonCodes.DailyLimitReached, decision.ReasonCode);
    }
}
