using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.UserActivity;

namespace PayPlanner.Api.Filters;

public class UserActivityLoggingFilter : IAsyncActionFilter
{
    private readonly IUserActivityService _activityService;
    private readonly ILogger<UserActivityLoggingFilter> _logger;

    public UserActivityLoggingFilter(
        IUserActivityService activityService,
        ILogger<UserActivityLoggingFilter> logger)
    {
        _activityService = activityService;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        if (!httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (httpContext.Request.Path.StartsWithSegments("/api/user-activity", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (string.Equals(httpContext.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
        var controllerName = descriptor?.ControllerName ?? "Unknown";
        var actionName = descriptor?.ActionName ?? httpContext.Request.Method;

        var objectId = ExtractObjectId(context);
        var metadata = BuildMetadataSnapshot(context);

        var stopwatch = Stopwatch.StartNew();
        ActionExecutedContext executedContext;
        try
        {
            executedContext = await next();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await WriteLogAsync(controllerName, actionName, objectId, metadata, UserActivityStatus.Failure, stopwatch.ElapsedMilliseconds, httpContext.Response?.StatusCode, ex: ex, cancellationToken: httpContext.RequestAborted);
            throw;
        }

        stopwatch.Stop();

        var status = executedContext.Exception != null && !executedContext.ExceptionHandled
            ? UserActivityStatus.Failure
            : DetermineStatusByCode(executedContext.HttpContext.Response?.StatusCode);

        await WriteLogAsync(controllerName, actionName, objectId, metadata, status, stopwatch.ElapsedMilliseconds, executedContext.HttpContext.Response?.StatusCode, executedContext.Exception, httpContext.RequestAborted);
    }

    private async Task WriteLogAsync(
        string controllerName,
        string actionName,
        string? objectId,
        IReadOnlyDictionary<string, object?> metadata,
        UserActivityStatus status,
        long durationMs,
        int? httpStatusCode,
        Exception? ex = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var description = BuildDescription(controllerName, actionName, objectId, httpStatusCode, ex);
            await _activityService.TryWriteAsync(new ActivityLogEntry
            {
                Category = controllerName,
                Action = actionName,
                Section = controllerName,
                ObjectId = objectId,
                ObjectType = controllerName,
                Description = description,
                Status = status,
                DurationMs = durationMs,
                HttpStatusCode = httpStatusCode,
                Metadata = metadata
            }, cancellationToken);
        }
        catch (Exception logEx)
        {
            _logger.LogError(logEx, "Ошибка записи журнала активности");
        }
    }

    private static string BuildDescription(string controller, string action, string? objectId, int? statusCode, Exception? ex)
    {
        var parts = new List<string>
        {
            $"{controller}.{action}"
        };

        if (!string.IsNullOrWhiteSpace(objectId))
        {
            parts.Add($"Id={objectId}");
        }

        if (statusCode.HasValue)
        {
            parts.Add($"HTTP {statusCode.Value}");
        }

        if (ex != null)
        {
            parts.Add($"Ошибка: {ex.GetType().Name} {ex.Message}");
        }

        return string.Join(" | ", parts);
    }

    private static IReadOnlyDictionary<string, object?> BuildMetadataSnapshot(ActionExecutingContext context)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in context.ActionArguments)
        {
            metadata[key] = SimplifyArgument(value);
        }

        return metadata;
    }

    private static object? SimplifyArgument(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
        }

        if (value.GetType().IsPrimitive || value is decimal || value is DateTime || value is Guid)
        {
            return value;
        }

        if (value is LoginRequest login)
        {
            return new { login.Email };
        }

        if (value is CreateUserActivityLogRequest)
        {
            return "UserActivityPayload";
        }

        return value.GetType().Name;
    }

    private static string? ExtractObjectId(ActionExecutingContext context)
    {
        if (context.RouteData.Values.TryGetValue("id", out var idValue) && idValue is not null)
        {
            return idValue.ToString();
        }

        if (context.ActionArguments.TryGetValue("id", out var argumentValue) && argumentValue is not null)
        {
            return argumentValue.ToString();
        }

        return null;
    }

    private static UserActivityStatus DetermineStatusByCode(int? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return UserActivityStatus.Info;
        }

        if (statusCode.Value >= 500)
        {
            return UserActivityStatus.Failure;
        }

        if (statusCode.Value >= 400)
        {
            return UserActivityStatus.Warning;
        }

        if (statusCode.Value >= 200)
        {
            return UserActivityStatus.Success;
        }

        return UserActivityStatus.Info;
    }
}
