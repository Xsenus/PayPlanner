using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireSectionPermissionAttribute : TypeFilterAttribute
{
    public RequireSectionPermissionAttribute(string section, string operation = RequireSectionPermissionOperations.View)
        : base(typeof(RequireSectionPermissionFilter))
    {
        Section = section;
        Operation = operation;
        Arguments = [section, operation];
    }

    public string Section { get; }
    public string Operation { get; }
}

public static class RequireSectionPermissionOperations
{
    public const string View = "view";
    public const string Create = "create";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Export = "export";
    public const string Analytics = "analytics";
}

public sealed class RequireSectionPermissionFilter : IAsyncActionFilter
{
    private readonly PaymentContext _db;
    private readonly string _section;
    private readonly string _operation;

    public RequireSectionPermissionFilter(string section, string operation, PaymentContext db)
    {
        _db = db;
        _section = section;
        _operation = operation;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var allowed = await _db.HasUserSectionPermissionAsync(
            context.HttpContext.User,
            _section,
            IsAllowed,
            context.HttpContext.RequestAborted);

        if (!allowed)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }

    private bool IsAllowed(RolePermissionValues permission)
        => _operation switch
        {
            RequireSectionPermissionOperations.View => permission.CanView,
            RequireSectionPermissionOperations.Create => permission.CanCreate,
            RequireSectionPermissionOperations.Edit => permission.CanEdit,
            RequireSectionPermissionOperations.Delete => permission.CanDelete,
            RequireSectionPermissionOperations.Export => permission.CanExport,
            RequireSectionPermissionOperations.Analytics => permission.CanViewAnalytics,
            _ => false,
        };
}
