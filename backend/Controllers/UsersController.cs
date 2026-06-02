using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Policy = "Admin")]
public class UsersController : ControllerBase
{
    private readonly AuthService _svc;

    public UsersController(AuthService svc) => _svc = svc;

    [HttpPost("{id:int}/approve")]
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

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        try
        {
            var dto = await _svc.CreateUserAsync(req);
            return dto is null
                ? Conflict(new { message = "Пользователь с таким email уже существует." })
                : Created($"/api/users/{dto.Id}", dto);
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

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return await _svc.DeleteUserAsync(id, actorUserId.Value) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserInUseAsResponsible")
        {
            return Conflict(new { message = "Нельзя удалить пользователя: он назначен ответственным в документах." });
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

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await _svc.GetAllRolesAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetUser(int id)
        => (await _svc.GetUserByIdAsync(id)) is { } u ? Ok(u) : NotFound();

    [HttpGet]
    public async Task<IActionResult> GetUsers([FromQuery] string? status)
        => Ok(await _svc.GetAllUsersAsync(status));

    [HttpGet("paged")]
    public async Task<IActionResult> GetUsersPaged(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
        => Ok(await _svc.GetUsersPagedAsync(status, page, pageSize));

    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> RejectUser(int id, [FromBody] RejectUserRequest? request)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return await _svc.RejectUserAsync(id, actorUserId.Value, request?.Reason)
                ? Ok(new { rejected = true })
                : NotFound();
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

    [HttpPost("merge")]
    public async Task<IActionResult> MergeUsers([FromBody] MergeUsersRequest request, CancellationToken cancellationToken)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _svc.MergeUsersAsync(request, actorUserId.Value, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "UserMergeFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return (await _svc.UpdateUserAsync(id, req, actorUserId.Value)) is { } u ? Ok(u) : NotFound();
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
}
