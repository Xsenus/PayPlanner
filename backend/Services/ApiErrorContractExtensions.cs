using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PayPlanner.Api.Services;

public static class ApiErrorContractExtensions
{
    public static IApplicationBuilder UseApiErrorContract(
        this IApplicationBuilder app,
        IWebHostEnvironment environment)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("PayPlanner.ApiErrorContract");
                if (exception is PayPlanner.Api.Services.Companies.SellerSnapshotTooLargeException tooLarge)
                {
                    // Ожидаемая ситуация данных профиля, а не сбой сервера: сообщаем, что исправить.
                    logger.LogWarning("Seller snapshot is too large: {Length}/{Limit}", tooLarge.Length, tooLarge.Limit);
                    await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "SellerSnapshotTooLarge", tooLarge.Message);
                    return;
                }

                logger.LogError(exception, "Unhandled API exception");

                await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "UnhandledServerError",
                    environment.IsDevelopment() && exception is not null
                        ? exception.Message
                        : "Произошла внутренняя ошибка сервера.");
            });
        });

        app.UseStatusCodePages(async statusCodeContext =>
        {
            var context = statusCodeContext.HttpContext;
            if (!IsApiRequest(context)
                || context.Response.HasStarted
                || !string.IsNullOrWhiteSpace(context.Response.ContentType))
            {
                return;
            }

            await WriteProblemAsync(
                context,
                context.Response.StatusCode,
                GetProblemTitle(context.Response.StatusCode),
                GetProblemDetail(context.Response.StatusCode));
        });

        return app;
    }

    private static bool IsApiRequest(HttpContext context)
        => context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    private static Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;

        return context.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json");
    }

    private static string GetProblemTitle(int statusCode)
        => statusCode switch
        {
            StatusCodes.Status400BadRequest => "BadRequest",
            StatusCodes.Status401Unauthorized => "Unauthorized",
            StatusCodes.Status403Forbidden => "Forbidden",
            StatusCodes.Status404NotFound => "NotFound",
            StatusCodes.Status409Conflict => "Conflict",
            StatusCodes.Status429TooManyRequests => "TooManyRequests",
            StatusCodes.Status500InternalServerError => "InternalServerError",
            StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
            _ => $"Http{statusCode}"
        };

    private static string GetProblemDetail(int statusCode)
        => statusCode switch
        {
            StatusCodes.Status400BadRequest => "Запрос не может быть обработан.",
            StatusCodes.Status401Unauthorized => "Требуется авторизация.",
            StatusCodes.Status403Forbidden => "Недостаточно прав для выполнения действия.",
            StatusCodes.Status404NotFound => "Ресурс не найден.",
            StatusCodes.Status409Conflict => "Запрос конфликтует с текущим состоянием данных.",
            StatusCodes.Status429TooManyRequests => "Слишком много запросов.",
            StatusCodes.Status500InternalServerError => "Произошла внутренняя ошибка сервера.",
            StatusCodes.Status503ServiceUnavailable => "Сервис временно недоступен.",
            _ => "Запрос завершился ошибкой."
        };
}
