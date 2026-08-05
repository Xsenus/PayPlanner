using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class AnonymousIntegrationSecurityTests
{
    [Fact]
    public void AnonymousIntegrationEndpointsHaveCentralLimitsAndBoundedBodies()
    {
        AssertPolicy(typeof(GoogleCalendarController), nameof(GoogleCalendarController.Callback), AnonymousIntegrationRateLimitPolicies.OAuthCallback);
        var googleWebhook = AssertPolicy(typeof(GoogleCalendarController), nameof(GoogleCalendarController.Webhook), AnonymousIntegrationRateLimitPolicies.Webhook);
        AssertRequestSizeLimit(googleWebhook, 16 * 1024);
        AssertPolicy(typeof(YandexCalendarController), nameof(YandexCalendarController.Callback), AnonymousIntegrationRateLimitPolicies.OAuthCallback);
        AssertPolicy(typeof(YandexDiskController), nameof(YandexDiskController.OAuthCallback), AnonymousIntegrationRateLimitPolicies.OAuthCallback);

        var telegramWebhook = AssertPolicy(typeof(NotificationsController), nameof(NotificationsController.TelegramWebhook), AnonymousIntegrationRateLimitPolicies.Webhook);
        AssertRequestSizeLimit(telegramWebhook, 256 * 1024);
        var maxWebhook = AssertPolicy(typeof(MaxWebhookController), nameof(MaxWebhookController.Receive), AnonymousIntegrationRateLimitPolicies.Webhook);
        AssertRequestSizeLimit(maxWebhook, 64 * 1024);

        AssertPolicy(typeof(FeatureFlagsController), nameof(FeatureFlagsController.Get), AnonymousIntegrationRateLimitPolicies.PublicMetadata);
        AssertPolicy(typeof(HealthController), nameof(HealthController.Get), AnonymousIntegrationRateLimitPolicies.PublicMetadata);

        var diagnostics = Assert.IsAssignableFrom<MethodInfo>(
            typeof(FeatureFlagsController).GetMethod(nameof(FeatureFlagsController.GetLegalIntegrationDiagnostics)));
        var authorization = Assert.Single(diagnostics.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("admin", authorization.Roles);
        Assert.Empty(diagnostics.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task IntegrationPoliciesRejectExcessRequestsWithoutSharingDifferentRoutes()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(options =>
                    {
                        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        AnonymousIntegrationRateLimitPolicies.AddPolicies(options);
                    });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/oauth/google", () => Results.Ok())
                            .RequireRateLimiting(AnonymousIntegrationRateLimitPolicies.OAuthCallback);
                        endpoints.MapGet("/oauth/yandex", () => Results.Ok())
                            .RequireRateLimiting(AnonymousIntegrationRateLimitPolicies.OAuthCallback);
                        endpoints.MapPost("/webhook/google", () => Results.Ok())
                            .RequireRateLimiting(AnonymousIntegrationRateLimitPolicies.Webhook);
                    });
                });
            })
            .StartAsync();
        var client = host.GetTestClient();

        await AssertThresholdAsync(client, HttpMethod.Get, "/oauth/google", 30);
        Assert.Equal(StatusCodes.Status200OK, (int)(await client.GetAsync("/oauth/yandex")).StatusCode);
        await AssertThresholdAsync(client, HttpMethod.Post, "/webhook/google", 300);
    }

    [Fact]
    public void ActivityLogQuerySanitizerRemovesReusableSecretsAndBoundsStoredValue()
    {
        var sanitized = Assert.IsType<string>(UserActivityService.SanitizeQueryString(
            $"?page=calendar&state=oauth-state&code=oauth-code&access_token=access-secret&customToken=custom-secret&filter={new string('a', 800)}"));

        Assert.True(sanitized.Length <= 512);
        Assert.DoesNotContain("oauth-state", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth-code", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-secret", sanitized, StringComparison.Ordinal);
        var parsed = QueryHelpers.ParseQuery(sanitized);
        Assert.Equal("calendar", parsed["page"]);
        Assert.Equal("[скрыто]", parsed["state"]);
        Assert.Equal("[скрыто]", parsed["code"]);
        Assert.Equal("[скрыто]", parsed["access_token"]);
        Assert.Equal("[скрыто]", parsed["customToken"]);
    }

    private static MethodInfo AssertPolicy(Type controllerType, string methodName, string policyName)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(controllerType.GetMethod(methodName));
        Assert.Equal(policyName, method.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        return method;
    }

    private static void AssertRequestSizeLimit(MemberInfo member, long expectedBytes)
    {
        var attribute = Assert.Single(member.CustomAttributes, item => item.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(expectedBytes, Assert.IsType<long>(Assert.Single(attribute.ConstructorArguments).Value));
    }

    private static async Task AssertThresholdAsync(HttpClient client, HttpMethod method, string path, int permitLimit)
    {
        for (var index = 0; index < permitLimit; index++)
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, path));
            Assert.Equal(StatusCodes.Status200OK, (int)response.StatusCode);
        }

        using var rejected = await client.SendAsync(new HttpRequestMessage(method, path));
        Assert.Equal(StatusCodes.Status429TooManyRequests, (int)rejected.StatusCode);
    }
}
