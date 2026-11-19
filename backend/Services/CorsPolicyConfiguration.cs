using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace PayPlanner.Api.Services;

public static class CorsPolicyConfiguration
{
    public static void ConfigureDefaultPolicy(
        CorsOptions options,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        options.AddDefaultPolicy(policy =>
        {
            // Имя скачиваемого файла приходит в Content-Disposition. Явное открытие
            // заголовка позволяет доверенному frontend сохранить исходное имя.
            // X-Upload-Key подтверждает идентификатор сохранённой попытки создания.
            policy.WithExposedHeaders(HeaderNames.ContentDisposition, "X-Upload-Key");
            if (environment.IsDevelopment())
            {
                policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                return;
            }

            var allowedOrigins = ResolveAllowedOrigins(configuration).ToArray();
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins);
            }

            policy.AllowAnyMethod().AllowAnyHeader();
        });
    }

    private static IEnumerable<string> ResolveAllowedOrigins(IConfiguration configuration)
    {
        var values = configuration
            .GetSection("Cors:AllowedOrigins")
            .GetChildren()
            .Select(child => child.Value)
            .Concat(SplitInlineOrigins(configuration["Cors:AllowedOrigins"]));

        return values
            .Select(NormalizeOrigin)
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static IEnumerable<string?> SplitInlineOrigins(string? rawOrigins)
        => string.IsNullOrWhiteSpace(rawOrigins)
            ? []
            : rawOrigins.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? NormalizeOrigin(string? origin)
    {
        var normalized = origin?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.TrimEnd('/');
    }
}
