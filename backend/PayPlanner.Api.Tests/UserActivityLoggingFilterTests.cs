using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public class UserActivityLoggingFilterTests
{
    [Fact]
    public async Task OnActionExecutionAsync_DoesNotStoreCancellationTokenInMetadata()
    {
        var service = new CapturingUserActivityService();
        var filter = new UserActivityLoggingFilter(
            service,
            NullLogger<UserActivityLoggingFilter>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/clients/42";
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(new RouteValueDictionary(new { id = 42 })),
            new ControllerActionDescriptor
            {
                ControllerName = "Clients",
                ActionName = "GetById",
            });
        var executingContext = new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>
            {
                ["id"] = 42,
                ["cancellationToken"] = CancellationToken.None,
            },
            controller: new object());

        await filter.OnActionExecutionAsync(
            executingContext,
            () => Task.FromResult(new ActionExecutedContext(executingContext, [], new object())));

        Assert.NotNull(service.LastEntry);
        var entry = service.LastEntry!;
        Assert.NotNull(entry.Metadata);
        var metadata = entry.Metadata!;
        Assert.Equal(42, metadata["id"]);
        Assert.DoesNotContain("cancellationToken", metadata.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CapturingUserActivityService : IUserActivityService
    {
        public ActivityLogEntry? LastEntry { get; private set; }

        public Task<UserActivitySettingsState> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserActivitySettingsState(true, 0, DateTime.UtcNow));

        public Task<UserActivitySettingsState> UpdateSettingsAsync(
            bool isEnabled,
            int? retentionDays,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new UserActivitySettingsState(isEnabled, retentionDays, DateTime.UtcNow));

        public Task<UserActivityLog?> TryWriteAsync(
            ActivityLogEntry entry,
            CancellationToken cancellationToken = default)
        {
            LastEntry = entry;
            return Task.FromResult<UserActivityLog?>(new UserActivityLog());
        }
    }
}
