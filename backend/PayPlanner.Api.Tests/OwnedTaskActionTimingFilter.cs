using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PayPlanner.Api.Tests;

// Installed only by the guarded synthetic hosting startup. Never reads arguments or results.
internal sealed class OwnedTaskActionTimingFilter(OwnedTaskReadDiagnostics diagnostics) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next();
        }
        finally
        {
            diagnostics.ObserveHandler(context.HttpContext, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
