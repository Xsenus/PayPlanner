using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class UserActivityAuditCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserAbortAfterActionDoesNotEraseItsAuditOrReplaceItsFailure(bool failedAction)
    {
        using var request = new CancellationTokenSource();
        var service = new AuditService();
        var logger = new AuditLogger();
        var filter = new UserActivityLoggingFilter(service, logger);
        var context = Context(request.Token);
        var original = new OperationCanceledException(request.Token);
        var actionCalls = 0;
        Task<ActionExecutedContext> Next()
        {
            actionCalls++;
            request.Cancel();
            if (failedAction) throw original;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        }

        if (failedAction)
            Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => filter.OnActionExecutionAsync(context, Next)));
        else
            await filter.OnActionExecutionAsync(context, Next);

        Assert.Equal(1, actionCalls);
        Assert.Equal(1, service.Writes);
        Assert.NotNull(service.Saved);
        Assert.Equal(failedAction ? UserActivityStatus.Failure : UserActivityStatus.Success, service.Saved!.Status);
        Assert.NotEqual(request.Token, service.WriteToken);
        Assert.True(service.WriteToken.CanBeCanceled);
        Assert.Equal(0, logger.Errors);
        Assert.Equal(StatusCodes.Status200OK, context.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task AuditWriteHasItsOwnBoundedCancellationWithoutCancellingTheRequest()
    {
        using var request = new CancellationTokenSource();
        var service = new AuditService { Stall = true };
        var logger = new AuditLogger();
        var filter = new UserActivityLoggingFilter(service, logger);
        var context = Context(request.Token);
        var write = filter.OnActionExecutionAsync(context,
            () => Task.FromResult(new ActionExecutedContext(context, [], new object())));
        try { await write.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally
        {
            if (!write.IsCompleted)
            {
                request.Cancel();
                await write.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Assert.Equal(1, service.Writes);
        Assert.True(service.WriteToken.IsCancellationRequested);
        Assert.False(request.IsCancellationRequested);
        Assert.Null(service.Saved);
        Assert.Equal(1, logger.Errors);
    }

    [Fact]
    public async Task GenuineAuditFailureRemainsAnErrorAndDoesNotReplaceTheActionResult()
    {
        var service = new AuditService { Fail = true };
        var logger = new AuditLogger();
        var filter = new UserActivityLoggingFilter(service, logger);
        var context = Context(CancellationToken.None);
        await filter.OnActionExecutionAsync(context,
            () => Task.FromResult(new ActionExecutedContext(context, [], new object())));
        Assert.Equal(1, service.Writes);
        Assert.Null(service.Saved);
        Assert.Equal(1, logger.Errors);
        Assert.Equal(StatusCodes.Status200OK, context.HttpContext.Response.StatusCode);
    }

    private static ActionExecutingContext Context(CancellationToken request)
    {
        var http = new DefaultHttpContext { RequestAborted = request };
        http.Request.Path = "/api/clients/42";
        http.Request.Method = HttpMethods.Get;
        http.Response.StatusCode = StatusCodes.Status200OK;
        var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor
        { ControllerName = "Clients", ActionName = "GetById" });
        return new ActionExecutingContext(action, [], new Dictionary<string, object?> { ["id"] = 42 }, new object());
    }

    private sealed class AuditService : IUserActivityService
    {
        public bool Stall { get; init; }
        public bool Fail { get; init; }
        public int Writes { get; private set; }
        public ActivityLogEntry? Saved { get; private set; }
        public CancellationToken WriteToken { get; private set; }
        public Task<UserActivitySettingsState> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserActivitySettingsState(true, 0, DateTime.UtcNow));
        public Task<UserActivitySettingsState> UpdateSettingsAsync(bool isEnabled, int? retentionDays, CancellationToken cancellationToken = default)
            => Task.FromResult(new UserActivitySettingsState(isEnabled, retentionDays, DateTime.UtcNow));
        public async Task<UserActivityLog?> TryWriteAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default)
        {
            Writes++;
            WriteToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (Stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Fail) throw new InvalidOperationException("Owned audit failure.");
            Saved = entry;
            return new UserActivityLog();
        }
    }

    private sealed class AuditLogger : ILogger<UserActivityLoggingFilter>
    {
        public int Errors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        { if (logLevel >= LogLevel.Error) Errors++; }
    }
}
