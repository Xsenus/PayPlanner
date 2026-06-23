using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/auth/admin")]
[Authorize(Policy = "Admin")]
public class AdminUsersController : ControllerBase, IAsyncActionFilter
{
    private readonly PaymentContext _db;
    private readonly AuthService _svc;

    public AdminUsersController(AuthService svc, PaymentContext db)
    {
        _svc = svc;
        _db = db;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = context.HttpContext.User.GetUserId();
        if (!userId.HasValue || !await _db.IsUserAdminAsync(userId.Value, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] string? status)
        => Ok(await _svc.GetAllUsersAsync(status));

    [HttpGet("users/{id:int}")]
    public async Task<IActionResult> GetUser(int id)
    {
        var user = await _svc.GetUserByIdAsync(id);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        try
        {
            var dto = await _svc.CreateUserAsync(req);
            return dto is null
                ? Conflict(new { message = "Пользователь с таким email уже существует." })
                : Created($"/api/auth/users/{dto.Id}", dto);
        }
        catch (InvalidOperationException ex) when (ex.Message == "RoleNotFound")
        {
            return BadRequest(new { message = "Выбранная роль не найдена." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotAllowed")
        {
            return BadRequest(new { message = "Сотрудника можно привязать только к основным компаниям из настроек." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotFound")
        {
            return BadRequest(new { message = "Одна из выбранных компаний не найдена." });
        }
    }

    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var dto = await _svc.UpdateUserAsync(id, req, actorUserId.Value);
            return dto is null ? NotFound() : Ok(dto);
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "ProtectedUserManagedOnlyBySelf"
            || ex.Message == "ProtectedUserEmailChangeForbidden"
            || ex.Message == "ProtectedUserAdministrativeFieldsLocked"
            || ex.Message == "ProtectedUserEmailReserved")
        {
            return Conflict(new
            {
                message = "Изменение системного администратора ограничено: этот пользователь управляет собой только самостоятельно, а его email зарезервирован."
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "RoleNotFound")
        {
            return BadRequest(new { message = "Выбранная роль не найдена." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserAlreadyExists")
        {
            return Conflict(new { message = "Пользователь с таким email уже существует." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotAllowed")
        {
            return BadRequest(new { message = "Сотрудника можно привязать только к основным компаниям из настроек." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotFound")
        {
            return BadRequest(new { message = "Одна из выбранных компаний не найдена." });
        }
    }

    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return (await _svc.DeleteUserAsync(id, actorUserId.Value)) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "ProtectedUserCannotBeDeleted")
        {
            return Conflict(new { message = "Системного администратора нельзя удалить." });
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("ExternalCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("GoogleCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("YandexCalendarCleanupFailed", StringComparison.Ordinal))
        {
            return Conflict(new
            {
                message = "Не удалось удалить календарь PayPlanner из внешнего календаря. Пользователь не удален, повторите операцию после проверки подключений календарей.",
                detail = ex.Message
            });
        }
    }

    [HttpPost("users/{id:int}/approve")]
    public async Task<IActionResult> ApproveUser(int id)
    {
        var s = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(s, out var approverId))
        {
            return Unauthorized();
        }

        var ok = await _svc.ApproveUserAsync(id, approverId);
        return ok ? Ok(new { approved = true }) : NotFound();
    }

    [HttpPost("users/{id:int}/reject")]
    public async Task<IActionResult> RejectUser(int id, [FromBody] RejectUserRequest? request)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var ok = await _svc.RejectUserAsync(id, actorUserId.Value, request?.Reason);
            return ok ? Ok(new { rejected = true }) : NotFound();
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "ProtectedUserManagedOnlyBySelf"
            || ex.Message == "ProtectedUserCannotBeDeleted")
        {
            return Conflict(new { message = "Системного администратора нельзя отклонить или удалить." });
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("ExternalCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("GoogleCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("YandexCalendarCleanupFailed", StringComparison.Ordinal))
        {
            return Conflict(new
            {
                message = "Не удалось удалить календарь PayPlanner из внешнего календаря. Пользователь не отклонен, повторите операцию после проверки подключений календарей.",
                detail = ex.Message
            });
        }
    }

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await _svc.GetAllRolesAsync());
}
