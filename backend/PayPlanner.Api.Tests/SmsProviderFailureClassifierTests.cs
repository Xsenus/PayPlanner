using System.Net;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsProviderFailureClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, SmsProviderFailureCodes.ProviderUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, SmsProviderFailureCodes.RateLimited, true)]
    [InlineData(HttpStatusCode.InternalServerError, SmsProviderFailureCodes.ProviderUnavailable, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SmsProviderFailureCodes.ProviderUnavailable, true)]
    [InlineData(HttpStatusCode.Unauthorized, SmsProviderFailureCodes.AuthenticationFailed, false)]
    [InlineData(HttpStatusCode.Forbidden, SmsProviderFailureCodes.AuthenticationFailed, false)]
    [InlineData(HttpStatusCode.PaymentRequired, SmsProviderFailureCodes.InsufficientBalance, false)]
    [InlineData(HttpStatusCode.BadRequest, SmsProviderFailureCodes.RequestRejected, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, SmsProviderFailureCodes.RequestRejected, false)]
    public void Classify_MapsHttpStatusToCanonicalFailure(
        HttpStatusCode statusCode,
        string expectedErrorCode,
        bool expectedTransient)
    {
        var result = SmsProviderFailureClassifier.Classify(statusCode);

        Assert.False(result.Accepted);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(expectedTransient, result.IsTransient);
        Assert.Equal(
            expectedTransient ? SmsProviderMessageStatuses.Failed : SmsProviderMessageStatuses.Rejected,
            result.Status);
    }

    [Theory]
    [InlineData("insufficient-balance", SmsProviderFailureCodes.InsufficientBalance)]
    [InlineData("NOT ENOUGH BALANCE", SmsProviderFailureCodes.InsufficientBalance)]
    [InlineData("sender.not.allowed", SmsProviderFailureCodes.SenderForbidden)]
    [InlineData("invalid_phone", SmsProviderFailureCodes.InvalidRecipient)]
    [InlineData("phone-format", SmsProviderFailureCodes.InvalidRecipient)]
    [InlineData("invalid token", SmsProviderFailureCodes.AuthenticationFailed)]
    public void Classify_MapsPermanentBusinessCodeBeforeGenericHttpStatus(
        string providerCode,
        string expectedErrorCode)
    {
        var result = SmsProviderFailureClassifier.Classify(
            HttpStatusCode.BadRequest,
            providerCode,
            TimeSpan.FromSeconds(30));

        Assert.False(result.IsTransient);
        Assert.Equal(SmsProviderMessageStatuses.Rejected, result.Status);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Null(result.RetryAfter);
    }

    [Theory]
    [InlineData("rate-limit", SmsProviderFailureCodes.RateLimited)]
    [InlineData("TOO MANY REQUESTS", SmsProviderFailureCodes.RateLimited)]
    [InlineData("temporary.unavailable", SmsProviderFailureCodes.ProviderUnavailable)]
    [InlineData("gateway_timeout", SmsProviderFailureCodes.ProviderUnavailable)]
    public void Classify_MapsTransientBusinessCodeBeforeGenericHttpStatus(
        string providerCode,
        string expectedErrorCode)
    {
        var result = SmsProviderFailureClassifier.Classify(
            HttpStatusCode.BadRequest,
            providerCode,
            TimeSpan.FromSeconds(15));

        Assert.True(result.IsTransient);
        Assert.Equal(SmsProviderMessageStatuses.Failed, result.Status);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(15), result.RetryAfter);
    }

    [Fact]
    public void Classify_UsesSafeMessageAndDoesNotExposeUnknownProviderCode()
    {
        const string sensitiveProviderCode = "customer_79990000000_secret_token";

        var result = SmsProviderFailureClassifier.Classify(
            HttpStatusCode.BadRequest,
            sensitiveProviderCode,
            TimeSpan.FromSeconds(-1));

        Assert.Equal(SmsProviderFailureCodes.ProviderBusinessError, result.ErrorCode);
        Assert.DoesNotContain("7999", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task ClassifiedTransientFailure_IsRetriedByResilienceExecutor()
    {
        var provider = new ClassifiedFailureSmsProvider();
        var options = new SmsProviderResilienceOptions(
            TimeSpan.FromSeconds(1),
            2,
            TimeSpan.Zero,
            TimeSpan.Zero,
            5,
            TimeSpan.FromMinutes(1));
        var executor = new SmsProviderResilienceExecutor(provider, options);
        var request = new SmsProviderSendRequest(
            "+79990000000",
            "Тест",
            "PayPlanner",
            "pp-sms-v1-classifier");

        var result = await executor.SendAsync(request);

        Assert.True(result.Accepted);
        Assert.Equal(2, provider.Attempts);
    }

    private sealed class ClassifiedFailureSmsProvider : ISmsProvider
    {
        public string Key => "classified";
        public int Attempts { get; private set; }

        public Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.FromResult(Attempts == 1
                ? SmsProviderFailureClassifier.Classify(HttpStatusCode.TooManyRequests)
                : new SmsProviderSendResult(
                    true,
                    "provider-message-1",
                    SmsProviderMessageStatuses.Accepted));
        }

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
