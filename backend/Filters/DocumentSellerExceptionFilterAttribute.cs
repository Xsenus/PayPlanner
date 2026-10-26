using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Filters;

/// <summary>Only the explicit seller protocol errors are translated; other failures remain failures.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DocumentSellerExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        IActionResult? result = context.Exception switch
        {
            SellerSnapshotConflictException error => new ConflictObjectResult(new { message = error.Message }),
            SellerSelectionException error => new BadRequestObjectResult(new { message = error.Message }),
            SellerSnapshotTooLargeException error => new BadRequestObjectResult(new { message = error.Message }),
            _ => null,
        };
        if (result is null) return;
        context.Result = result;
        context.ExceptionHandled = true;
    }
}
