using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PublicAccessSecurityTests
{
    [Fact]
    public void PublicControllersUseCentralLimitsSmallBodiesAndBoundedTokens()
    {
        Assert.Equal(
            PublicAccessRateLimitPolicies.WorkOrderApproval,
            typeof(PublicWorkOrderApprovalController)
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(
            PublicAccessRateLimitPolicies.ClientPortal,
            typeof(ClientPortalController)
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        AssertRequestSizeLimit(typeof(PublicWorkOrderApprovalController), 16 * 1024);
        AssertRequestSizeLimit(typeof(ClientPortalController), 16 * 1024);

        var callback = Assert.IsAssignableFrom<MethodInfo>(
            typeof(PublicMessengerDocumentDeliveriesController)
                .GetMethod(nameof(PublicMessengerDocumentDeliveriesController.Callback)));
        Assert.Equal(
            PublicAccessRateLimitPolicies.MessengerDocumentCallback,
            callback.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        AssertRequestSizeLimit(callback, 16 * 1024);

        var secureDownload = Assert.IsAssignableFrom<MethodInfo>(
            typeof(PublicMessengerDocumentDeliveriesController)
                .GetMethod(nameof(PublicMessengerDocumentDeliveriesController.DownloadSecurely)));
        AssertRequestSizeLimit(secureDownload, 4 * 1024);
        Assert.Equal(
            PublicAccessRateLimitPolicies.MessengerDocumentDownload,
            secureDownload.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        var legacyDownload = Assert.IsAssignableFrom<MethodInfo>(
            typeof(PublicMessengerDocumentDeliveriesController)
                .GetMethod(nameof(PublicMessengerDocumentDeliveriesController.Download)));
        Assert.Equal(
            PublicAccessRateLimitPolicies.MessengerDocumentDownload,
            legacyDownload.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        AssertInvalid(new ClientPortalTokenRequest { Token = new string('x', 513) });
        AssertInvalid(new ClientPortalTokenRequest());
        AssertInvalid(new ClientPortalVerifyRequest { Token = "token", Otp = "1" });
        AssertInvalid(new ClientPortalVerifyRequest());
        AssertInvalid(new PublicWorkOrderApprovalOpenRequest { Token = new string('x', 513) });
        AssertInvalid(new PublicWorkOrderApprovalOpenRequest());
        AssertInvalid(new PublicWorkOrderApprovalDecisionRequest
        {
            Token = "token",
            Otp = "123456",
            Decision = "approved",
            Comment = new string('x', 2001),
        });
        AssertInvalid(new MessengerDocumentDeliveryCallbackRequest { ProviderMessageId = "", Status = "read" });
        AssertInvalid(new MessengerDocumentDeliveryCallbackRequest
        {
            ProviderMessageId = new string('x', 257),
            Status = "read",
        });
        AssertInvalid(new MessengerDocumentDownloadRequest { Token = new string('x', 513) });
    }

    [Fact]
    public async Task PublicPoliciesRejectRequestsAtConfiguredThresholds()
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
                        PublicAccessRateLimitPolicies.AddPolicies(options);
                    });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/work-order", () => Results.Ok())
                            .RequireRateLimiting(PublicAccessRateLimitPolicies.WorkOrderApproval);
                        endpoints.MapGet("/portal", () => Results.Ok())
                            .RequireRateLimiting(PublicAccessRateLimitPolicies.ClientPortal);
                        endpoints.MapGet("/document", () => Results.Ok())
                            .RequireRateLimiting(PublicAccessRateLimitPolicies.MessengerDocumentDownload);
                        endpoints.MapGet("/callback", () => Results.Ok())
                            .RequireRateLimiting(PublicAccessRateLimitPolicies.MessengerDocumentCallback);
                    });
                });
            })
            .StartAsync();
        var client = host.GetTestClient();

        await AssertThresholdAsync(client, "/work-order", 10);
        await AssertThresholdAsync(client, "/portal", 30);
        await AssertThresholdAsync(client, "/document", 30);
        await AssertThresholdAsync(client, "/callback", 300);
    }

    private static async Task AssertThresholdAsync(HttpClient client, string path, int permitLimit)
    {
        for (var index = 0; index < permitLimit; index++)
        {
            Assert.Equal(StatusCodes.Status200OK, (int)(await client.GetAsync(path)).StatusCode);
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests, (int)(await client.GetAsync(path)).StatusCode);
    }

    private static void AssertInvalid(object model)
    {
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), results, true));
        Assert.NotEmpty(results);
    }

    private static void AssertRequestSizeLimit(MemberInfo member, long expectedBytes)
    {
        var attribute = Assert.Single(
            member.CustomAttributes,
            item => item.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(expectedBytes, Assert.IsType<long>(Assert.Single(attribute.ConstructorArguments).Value));
    }
}
