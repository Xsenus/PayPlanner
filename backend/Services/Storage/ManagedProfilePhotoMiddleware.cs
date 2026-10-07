using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Storage;

// Profile photos already have public static URLs. This intercepts only the current
// photo binding; it does not expose other catalog objects or fetch remote images.
public sealed class ManagedProfilePhotoMiddleware(RequestDelegate next, IOptionsMonitor<StorageOrchestrationOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled || !settings.ProductReadEnabledClasses.Contains("profile-photo", StringComparer.Ordinal)
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            || !context.Request.Path.StartsWithSegments(UserPhotoStoragePaths.RequestPath, out var remaining))
        {
            await next(context);
            return;
        }
        var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not { Length: 2 }
            || !int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
        {
            await next(context);
            return;
        }
        var contentType = Path.GetExtension(segments[1]).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };
        if (contentType is null) { await next(context); return; }
        var db = context.RequestServices.GetRequiredService<PaymentContext>();
        var photoUrl = await db.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => user.PhotoUrl).SingleOrDefaultAsync(context.RequestAborted);
        var photoPath = photoUrl;
        if (Uri.TryCreate(photoUrl, UriKind.Absolute, out var absolute)) photoPath = absolute.AbsolutePath;
        if (!string.Equals(photoPath, context.Request.Path.Value, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }
        var reader = context.RequestServices.GetRequiredService<IProductStorageReader>();
        var result = await reader.OpenReadAsync(LegacyStorageSources.ProfilePhotos, userId, photoUrl!, "image/*", context.RequestAborted);
        if (result.Status == ProductStorageReadStatus.Legacy) { await next(context); return; }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Do not cache a failed verification: a healthy replica may become available later.
        context.Response.Headers.CacheControl = "no-store";
        if (result.Status != ProductStorageReadStatus.Available || result.File is null)
        {
            if (result.File is not null) await result.File.DisposeAsync();
            context.Response.StatusCode = result.Status == ProductStorageReadStatus.Deleted
                ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable;
            return;
        }
        await using var file = result.File;
        await Results.File(file.Content, contentType, enableRangeProcessing: true).ExecuteAsync(context);
    }
}
