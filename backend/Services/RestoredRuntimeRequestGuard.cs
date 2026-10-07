using Microsoft.AspNetCore.Http;

namespace PayPlanner.Api.Services;

/// <summary>Fails closed before authentication or business handlers in a restored probe.</summary>
public sealed class RestoredRuntimeRequestGuard(RequestDelegate next, IConfiguration configuration)
{
    public const string IdentityPath = "/api/restored-runtime/identity";
    public const string InstanceHeader = "X-PayPlanner-Restored-Instance";
    public const string InstanceQuery = "pp_restored_instance";

    public async Task InvokeAsync(HttpContext context)
    {
        var isolated = configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false);
        var handshake = context.Request.Path.Equals(new PathString(IdentityPath));
        var markedRequest = context.Request.Headers.ContainsKey(InstanceHeader) || context.Request.Query.ContainsKey(InstanceQuery);

        if (!isolated)
        {
            if (handshake || markedRequest)
            {
                context.Response.StatusCode = handshake ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
                return;
            }
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        var expected = RestoredRuntimeDatabaseIdentity.RequireInstanceId(configuration).ToString("N");
        if (handshake)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET";
                return;
            }
            await context.Response.WriteAsJsonAsync(new { isolated = true, instanceId = expected }, context.RequestAborted);
            return;
        }

        var values = context.Request.Headers[InstanceHeader];
        var query = context.Request.Query[InstanceQuery];
        var headerPresent = context.Request.Headers.ContainsKey(InstanceHeader);
        var queryPresent = context.Request.Query.ContainsKey(InstanceQuery);
        var validHeader = headerPresent && values.Count == 1 && string.Equals(values[0], expected, StringComparison.Ordinal);
        var validQuery = queryPresent && query.Count == 1 && string.Equals(query[0], expected, StringComparison.Ordinal);
        // Native browser WebSocket/SSE and image requests cannot carry custom headers.
        // Query-only identity is allowed for GET, never for a write or login POST.
        if ((headerPresent && !validHeader) || (queryPresent && !validQuery)
            || !(validHeader || (HttpMethods.IsGet(context.Request.Method) && validQuery)))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        await next(context);
    }
}
