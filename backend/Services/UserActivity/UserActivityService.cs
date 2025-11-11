using System.Security.Claims;
using System.Text.Json;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.UserActivity;

public class UserActivityService : IUserActivityService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly IDbContextFactory<PaymentContext> _contextFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<UserActivityService> _logger;

    public UserActivityService(
        IDbContextFactory<PaymentContext> contextFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<UserActivityService> logger)
    {
        _contextFactory = contextFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<UserActivityLog?> TryWriteAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;

            var userId = entry.UserId;
            string? userEmail = entry.UserEmail;
            string? userFullName = entry.UserFullName;

            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                if (userId is null && int.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed))
                {
                    userId = parsed;
                }

                userEmail ??= httpContext.User.FindFirstValue(ClaimTypes.Email);
                userFullName ??= httpContext.User.FindFirstValue(ClaimTypes.Name);
            }

            var ip = entry.IpAddress ?? httpContext?.Request?.Headers?["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = httpContext?.Connection?.RemoteIpAddress?.ToString();
            }

            var userAgent = entry.UserAgent ?? httpContext?.Request?.Headers?["User-Agent"].FirstOrDefault();
            var method = entry.HttpMethod ?? httpContext?.Request?.Method;
            var path = entry.Path ?? httpContext?.Request?.Path.ToString();
            var query = entry.QueryString ?? httpContext?.Request?.QueryString.ToString();
            var statusCode = entry.HttpStatusCode ?? httpContext?.Response?.StatusCode;

            string? metadataJson = null;
            if (entry.Metadata is not null && entry.Metadata.Count > 0)
            {
                metadataJson = JsonSerializer.Serialize(entry.Metadata, JsonOptions);
            }

            var log = new UserActivityLog
            {
                UserId = userId,
                UserEmail = string.IsNullOrWhiteSpace(userEmail) ? null : userEmail,
                UserFullName = string.IsNullOrWhiteSpace(userFullName) ? null : userFullName,
                Category = entry.Category,
                Action = entry.Action,
                Section = entry.Section,
                ObjectType = entry.ObjectType,
                ObjectId = entry.ObjectId,
                Description = entry.Description,
                Status = entry.Status,
                IpAddress = ip,
                UserAgent = userAgent,
                HttpMethod = method,
                Path = path,
                QueryString = query,
                HttpStatusCode = statusCode,
                DurationMs = entry.DurationMs,
                MetadataJson = metadataJson,
                CreatedAt = entry.CreatedAtUtc ?? DateTime.UtcNow
            };

            var lockTaken = false;
            try
            {
                await WriteLock.WaitAsync(cancellationToken);
                lockTaken = true;

                await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                context.UserActivityLogs.Add(log);
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                if (lockTaken)
                {
                    WriteLock.Release();
                }
            }

            return log;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить журнал активности пользователя");
            return null;
        }
    }
}
